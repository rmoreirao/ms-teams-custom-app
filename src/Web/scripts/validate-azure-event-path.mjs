import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { randomBytes } from "node:crypto";
import { request as httpsRequest } from "node:https";
import { readFileSync, writeFileSync, appendFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { expect } from "@playwright/test";
import { withFixtureSession } from "./check-entra-signin.mjs";
import { isLiveSession, SignInCheckError } from "./entra-signin.mjs";
import { waitForOriginalOperation } from "./teams-validation.mjs";

const inputPath = process.env.AZURE_LIVE_SETTINGS;
if (!inputPath || process.argv[2] !== "--verify-azure")
  throw new SignInCheckError("Select private AZURE_LIVE_SETTINGS and explicitly request --verify-azure.");
const parametersPath = resolve(inputPath);
/** @type {{schemaVersion:number, probeActorId:string, liveConfig:{
 * authorityMode:string, syntheticActorIds:string[],
 * resources:{id:string,title:string,kind:"chat"|"channel"}[]
 * }}} */
const target = JSON.parse(readFileSync(parametersPath, "utf8"));
if (target.schemaVersion !== 1 || target.liveConfig?.authorityMode !== "DelegatedSynthetic" ||
    target.liveConfig.syntheticActorIds?.length !== 3 || target.liveConfig.resources?.length !== 5)
  throw new SignInCheckError("The target is not the approved three-person/five-resource live scope.");
const privateDirectory = dirname(resolve(parametersPath));
const image = readFileSync(resolve(privateDirectory, "image.txt"), "utf8").trim();
const runId = randomUUID();
const repo = resolve(import.meta.dirname, "..", "..", "..");

/** @param {"Probe" | "VerifyProbe"} operation */
async function runOperation(operation) {
  const log = resolve(privateDirectory, `browser-${operation}-${runId}.log`);
  const childEnvironment = Object.fromEntries(Object.entries(process.env)
    .filter(([key]) => !/PASSWORD|TOTP|SECRET|TOKEN/i.test(key)));
  await new Promise((done, failed) => {
    const child = spawn("pwsh", ["-NoProfile", "-File", resolve(repo, "scripts", "Invoke-SyntheticLiveOperation.ps1"),
      "-ParametersPath", resolve(parametersPath), "-Image", image, "-Operation", operation, "-RunId", runId,
      "-Apply", "-Confirm:$false"], { cwd: repo, env: childEnvironment, stdio: ["ignore", "pipe", "pipe"] });
    child.stdout.on("data", chunk => appendFileSync(log, chunk));
    child.stderr.on("data", chunk => appendFileSync(log, chunk));
    child.once("error", () => failed(new SignInCheckError("The scoped Azure operation could not start; no automatic retry was attempted.")));
    child.once("close", code => code === 0 ? done(undefined) :
      failed(new SignInCheckError(`The scoped ${operation} failed. Inspect its private log and original journal; do not replay a possibly committed POST.`)));
  });
  const result = JSON.parse(readFileSync(resolve(privateDirectory, `operation-${operation}-${runId}.json`), "utf8"));
  if (result.operation !== operation || result.runId !== runId || result.proofs?.length !== 2)
    throw new SignInCheckError("The Azure operation did not return the exact original run evidence.");
  return result;
}

withFixtureSession("sam", async ({ context, page, settings, stage }) => {
  /** @type {{category:string,status:number,code?:string}[]} */
  const outcomes = [];
  /** @type {import("@playwright/test").Page[]} */
  const observedPages = [page];
  /** @type {{page:number,host:string,path:string,frames:number,error?:string,closed?:boolean}[]} */
  const sockets = [];
  /** @type {string[]} */
  const consoleErrors = [];
  /** @type {{host:string,path:string,algorithm?:string,audience?:string,expires?:number}[]} */
  const connectionTargets = [];
  /** @type {{ page: number, receivedAt: string, ids: string[] }[]} */
  const deltas = [];
  /** @param {string} text */
  const redact = text => text.replace(/(?:https?:\/\/|wss:\/\/)\S+/g, "[URL withheld]")
    .replace(/[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}/g, "[token withheld]").slice(0, 900);
  try {
  stage("verify scoped live mode");
  const current = await context.request.get(`${settings.origin}/api/session`).then(response => response.json());
  if (current.scopedValidation !== true || !isLiveSession(current))
    throw new SignInCheckError("The authenticated target is not the real scoped event-driven runtime.");
  const negotiated = await context.request.post(`${settings.origin}/api/realtime/negotiate`, {
    headers: { "X-CSRF-TOKEN": current.csrf }, data: {},
  }).then(response => response.json());
  if (negotiated.transport !== "webpubsub" || typeof negotiated.url !== "string")
    throw new SignInCheckError("The Live endpoint did not negotiate Web PubSub.");
  const socketUrl = new URL(negotiated.url);
  socketUrl.protocol = "https:";
  /** @param {string} protocol @param {string | undefined} origin
   * @returns {Promise<{status:number,reason:string,protocol?:string,code?:string,server?:string,headerNames?:string[]}>} */
  const checkUpgrade = (protocol, origin) => new Promise((complete, reject) => {
    const request = httpsRequest(socketUrl, {
      headers: { Connection: "Upgrade", Upgrade: "websocket", ...(origin ? { Origin: origin } : {}),
        "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": randomBytes(16).toString("base64"),
        "Sec-WebSocket-Protocol": protocol },
      timeout: 30_000,
    });
    request.once("upgrade", (response, socket) => {
      socket.destroy();
      complete({ status: response.statusCode ?? 0, reason: "upgraded", protocol: String(response.headers["sec-websocket-protocol"] ?? "") });
    });
    request.once("response", response => {
      let body = "";
      response.on("data", chunk => { if (body.length < 8192) body += String(chunk); });
      response.once("end", () => complete({ status: response.statusCode ?? 0, reason: redact(body),
        code: String(response.headers["x-ms-error-code"] ?? response.headers["x-webpubsub-error-code"] ?? ""),
        server: String(response.headers.server ?? ""), headerNames: Object.keys(response.headers) }));
    });
    request.once("error", () => reject(new SignInCheckError("The explicit owned-service WebSocket diagnostic could not connect.")));
    request.once("timeout", () => request.destroy(new Error("Owned-service handshake timeout")));
    request.end();
  });
  const upgrade = await checkUpgrade("json.reliable.webpubsub.azure.v1", settings.origin);
  if (upgrade.status !== 101) {
    console.error(JSON.stringify({ phase: "owned-service-websocket-upgrade", upgrade }));
    throw new SignInCheckError("The Web PubSub service rejected the authenticated WebSocket upgrade.");
  }
  await context.unroute(`${settings.origin}/api/**`);
  const appChat = `Azure event-driven validation ${runId} (app chat)`;
  const appReply = `Azure event-driven validation ${runId} (app reply)`;
  let posts = 0;
  await context.route(`${settings.origin}/api/**`, async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "GET" || path === "/api/realtime/negotiate") return route.continue();
    if (request.method() === "POST" && ["/api/conversations/graph-pilot-chat/messages",
      "/api/conversations/graph-pilot-channel/messages"].includes(path)) {
      const body = request.postDataJSON();
      if (body.body === appChat || body.body === appReply) { posts++; return route.continue(); }
    }
    return route.abort("blockedbyclient");
  });
  const channelPage = await context.newPage();
  observedPages.push(channelPage);
  const pages = [page, channelPage];
  const catalogCounts = [0, 0];
  const loadedViews = [false, false];
  /** @type {Set<string>} */
  const transports = new Set();
  for (const [index, currentPage] of pages.entries()) {
    currentPage.on("console", message => { if (message.type() === "error") consoleErrors.push(redact(message.text())); });
    currentPage.on("websocket", socket => {
      const url = new URL(socket.url());
      /** @type {{page:number,host:string,path:string,frames:number,error?:string,closed?:boolean}} */
      const state = { page: index, host: url.hostname, path: url.pathname, frames: 0, error: undefined, closed: false };
      sockets.push(state);
      socket.on("framereceived", () => { state.frames++; });
      socket.on("socketerror", error => { state.error = redact(error); });
      socket.on("close", () => { state.closed = true; });
    });
    currentPage.on("request", request => {
      if (new URL(request.url()).pathname === "/api/conversations") catalogCounts[index]++;
    });
    currentPage.on("response", async response => {
      const path = new URL(response.url()).pathname;
      if (path === `/api/conversations/${index === 0 ? "graph-pilot-chat" : "graph-pilot-channel"}/messages` &&
          response.request().method() === "GET") loadedViews[index] = response.status() === 200;
      if (new URL(response.url()).origin === settings.origin && path.startsWith("/api/")) {
        const category = path === "/api/conversations" ? "catalog" : path === "/api/realtime/negotiate" ? "negotiate" :
          path.endsWith("/messages") ? "messages" : path.endsWith("/changes") ? "changes" : path === "/api/people" ? "people" : "other";
        const outcome = { category, status: response.status(), code: undefined };
        if (response.status() >= 400) {
          const failed = await response.json();
          outcome.code = failed.code;
        }
        outcomes.push(outcome);
      }
      if (path === "/api/realtime/negotiate" && response.status() === 200) {
        /** @type {{transport:string,url:string}} */
        const data = await response.json();
        transports.add(data.transport);
        const url = new URL(data.url);
        const encoded = url.searchParams.get("access_token")?.split(".");
        const header = encoded?.[0] ? JSON.parse(Buffer.from(encoded[0], "base64url").toString("utf8")) : {};
        const claims = encoded?.[1] ? JSON.parse(Buffer.from(encoded[1], "base64url").toString("utf8")) : {};
        connectionTargets.push({ host: url.hostname, path: url.pathname, algorithm: header.alg, audience: claims.aud, expires: claims.exp });
      } else if (path.endsWith("/changes") && response.status() === 200) {
        /** @type {import("../src/api.js").ChangePage} */
        const data = await response.json();
        const matching = (data.messages ?? []).filter((message) => message.body?.includes(runId));
        if (matching.length) deltas.push({ page: index, receivedAt: new Date().toISOString(),
          ids: matching.map(message => message.id) });
      }
    });
  }
  stage("open both real PubSub observers");
  await Promise.all(pages.map(currentPage => currentPage.goto(settings.origin, { waitUntil: "domcontentloaded" })));
  const chat = target.liveConfig.resources.find(resource => resource.id === "graph-pilot-chat");
  const channel = target.liveConfig.resources.find(resource => resource.id === "graph-pilot-channel");
  if (!chat || !channel) throw new SignInCheckError("The bounded source resources are missing.");
  for (const currentPage of pages) {
    await expect(currentPage.locator(".connection")).toHaveText(/^(Live|Synchronization unavailable)$/, { timeout: 30_000 });
    if (await currentPage.locator(".connection").textContent() !== "Live")
      throw new SignInCheckError("The hosted PubSub socket could not establish an authorized connection.");
  }
  await page.getByRole("navigation", { name: "Conversations" }).locator(".conversation-row").filter({ hasText: chat.title }).click();
  await channelPage.getByRole("navigation", { name: "Conversations" }).locator(".conversation-row").filter({ hasText: channel.title }).click();
  await expect.poll(() => loadedViews.every(Boolean), { timeout: 120_000 }).toBe(true);
  if (!transports.has("webpubsub") || transports.has("poll")) throw new SignInCheckError("The hosted observers did not negotiate real Web PubSub.");
  stage("create the journaled Graph-originated probes once");
  const [sourceProof] = await Promise.all([
    runOperation("Probe"),
    expect(page.getByText(`Azure event-driven validation ${runId} (chat)`, { exact: true })).toBeVisible({ timeout: 150_000 }),
    expect(channelPage.getByText(`Azure event-driven validation ${runId} (channel)`, { exact: true })).toBeVisible({ timeout: 150_000 }),
  ]);
  for (const proof of sourceProof.proofs) {
    const observer = proof.resourceId === chat.id ? 0 : 1;
    if (!deltas.some(delta => delta.page === observer && delta.ids.includes(proof.messageId)))
      throw new SignInCheckError("A source message appeared without the exact observer change-feed evidence.");
    /** @type {import("../src/api.js").MessagePage} */
    const snapshot = await context.request.get(`${settings.origin}/api/conversations/${proof.resourceId}/messages`)
      .then(response => response.json());
    if (!snapshot.messages.some(message => message.id === proof.messageId && message.actorId === target.probeActorId))
      throw new SignInCheckError("The projected source message ID/author does not match Graph confirmation.");
  }
  stage("verify normal delegated application sends and channel replies");
  const chatResponse = page.waitForResponse(response => response.request().method() === "POST" &&
    new URL(response.url()).pathname === "/api/conversations/graph-pilot-chat/messages");
  await page.getByRole("textbox", { name: "Write a message", exact: true }).fill(appChat);
  await page.getByRole("button", { name: "Send", exact: true }).click();
  const confirmedChat = await waitForOriginalOperation(context.request, settings.origin, "graph-pilot-chat", await (await chatResponse).json());
  if (confirmedChat.state !== "confirmed" || !confirmedChat.messageId)
    throw new SignInCheckError(`The original application chat send was not Graph-confirmed (${confirmedChat.state}): ${redact(String(confirmedChat.error ?? ""))}. No resend was attempted.`);
  // Chat and channel commands share the same actor send budget.
  await page.waitForTimeout(1400);
  const parent = channelPage.locator(".message").filter({ hasText: `Azure event-driven validation ${runId} (channel)` });
  await parent.getByRole("button", { name: "Open thread" }).click();
  const replyResponse = channelPage.waitForResponse(response => response.request().method() === "POST" &&
    new URL(response.url()).pathname === "/api/conversations/graph-pilot-channel/messages");
  await channelPage.getByRole("textbox", { name: "Write a reply", exact: true }).fill(appReply);
  await channelPage.getByRole("button", { name: "Reply", exact: true }).click();
  const confirmedReply = await waitForOriginalOperation(context.request, settings.origin, "graph-pilot-channel", await (await replyResponse).json());
  if (confirmedReply.state !== "confirmed" || !confirmedReply.messageId)
    throw new SignInCheckError(`The original application reply was not Graph-confirmed (${confirmedReply.state}): ${redact(String(confirmedReply.error ?? ""))}. No resend was attempted.`);
  await expect(page.getByText(appChat, { exact: true })).toHaveCount(1);
  await expect(channelPage.getByText(appReply, { exact: true })).toHaveCount(1);
  const baseline = [...catalogCounts];
  stage("verify authenticated notification and durable delivery provenance");
  const pollingStartedAt = Date.now();
  const [provenance, noPollingWindowMilliseconds] = await Promise.all([
    runOperation("VerifyProbe"),
    (async () => {
      await page.waitForTimeout(31_000);
      if (catalogCounts.some((value, index) => value !== baseline[index]))
        throw new SignInCheckError("The observers reloaded the catalog during the measured 31-second verification window.");
      return Date.now() - pollingStartedAt;
    })(),
  ]);
  if (posts !== 2) throw new SignInCheckError("The browser issued an unexpected extra application send.");
  for (const currentPage of pages) {
    const storage = await currentPage.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }));
    if (storage.local || storage.session) throw new SignInCheckError("Business state was persisted in browser storage.");
  }
  await page.screenshot({ path: resolve(privateDirectory, `azure-chat-${runId}.png`) });
  await channelPage.setViewportSize({ width: 390, height: 844 });
  await channelPage.screenshot({ path: resolve(privateDirectory, `azure-channel-mobile-${runId}.png`) });
  const proof = {
    runId, testedAt: new Date().toISOString(), mode: "Live", transport: "webpubsub",
    graphSource: sourceProof.proofs, authenticatedNotificationAndDelivery: provenance.proofs, browserDeltas: deltas,
    appChat: { messageId: confirmedChat.messageId, state: confirmedChat.state },
    appReply: { messageId: confirmedReply.messageId, state: confirmedReply.state },
    noCatalogPolling: true, noPollingWindowMilliseconds, noExtraBrowserPosts: true, noBusinessBrowserStorage: true,
  };
  writeFileSync(resolve(privateDirectory, `azure-validation-${runId}.json`), JSON.stringify(proof, null, 2));
  return { runId, graphSourceMessagesVerified: 2, appSendsConfirmed: 2, teamsOperationsPerformed: true, transport: "webpubsub",
    authenticatedNotificationProvenance: true, noThirtySecondPolling: true };
  } catch (error) {
    const states = [];
    for (const currentPage of observedPages) {
      if (currentPage.isClosed() || new URL(currentPage.url()).origin !== settings.origin) continue;
      states.push({
        connection: await currentPage.locator(".connection").textContent().catch(() => "unavailable"),
        rows: await currentPage.locator(".conversation-row").count(),
        notices: (await currentPage.locator(".notice, .state[role=alert]").allTextContents()).map(value =>
          value.replace(/(?:https?:\/\/|wss:\/\/)\S+/g, "[URL withheld]").slice(0, 700)),
      });
    }
    writeFileSync(resolve(privateDirectory, `azure-failure-${runId}.json`), JSON.stringify({
      runId, states, outcomes, sockets, consoleErrors, connectionTargets, deltas, errorKind: error instanceof Error ? error.name : "unknown",
      postAuthFailure: true,
    }, null, 2));
    console.error(JSON.stringify({ runId, observerStates: states, apiOutcomes: outcomes.slice(-12), sockets, consoleErrors, connectionTargets }));
    throw error;
  }
}).catch(error => {
  console.error(error instanceof SignInCheckError ? error.message :
    "Azure event-path validation failed. Sensitive browser/operation details were withheld; inspect the private evidence.");
  process.exitCode = 1;
});
