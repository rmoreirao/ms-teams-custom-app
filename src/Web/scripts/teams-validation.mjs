import { expect } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { createHash } from "node:crypto";
import { isLiveSession, isServiceSession, SignInCheckError } from "./entra-signin.mjs";

/** @typedef {{ id: string, kind: "chat" | "channel", title: string }} LiveResource */
/** @typedef {{ schemaVersion: number, tenantId: string, webClientId: string, origin: string, actors: {persona:string,id:string,upn:string}[], resources: LiveResource[], directChatsEnabled?:boolean, authProfile?:"live"|"live-delegated"|"service" }} LiveMetadata */
/** @typedef {{ id: string, body: string, actorId: string }} GraphMessage */

/** @param {string} left @param {string} right */
const pairId = (left, right) => "graph-pilot-dm-" +
  createHash("sha256").update([left, right].sort().join(":")).digest("hex");

/** @param {LiveMetadata} metadata */
export function validateLiveMetadata(metadata) {
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (metadata?.schemaVersion !== 2 || !Array.isArray(metadata.actors) || metadata.actors.length !== 3 ||
      !Array.isArray(metadata.resources) || metadata.resources.length !== 5 ||
      ![metadata.tenantId, metadata.webClientId].every(value => guid.test(value)) ||
      !URL.canParse(metadata.origin) ||
      !["live", "live-delegated", "service"].includes(metadata.authProfile ?? "live-delegated"))
    throw new SignInCheckError("Migrate the private Live metadata to schemaVersion 2 with the approved three actors and five existing resources.");
  const origin = new URL(metadata.origin);
  if (origin.protocol !== "https:" || origin.origin !== metadata.origin || origin.username || origin.password ||
      new Set(metadata.actors.map(actor => actor.id)).size !== 3 ||
      new Set(metadata.actors.map(actor => actor.persona)).size !== 3 ||
      metadata.actors.some(actor => !["alex", "sam", "jordan"].includes(actor.persona) || !guid.test(actor.id) || !actor.upn))
    throw new SignInCheckError("The private Live metadata has an unexpected identity or HTTPS origin.");
  const approved = new Set(["graph-pilot-chat", "graph-pilot-channel",
    ...metadata.actors.flatMap((actor, index) => metadata.actors.slice(index + 1).map(other => pairId(actor.id, other.id)))]);
  if (new Set(metadata.resources.map(resource => resource.id)).size !== 5 ||
      metadata.resources.some(resource => !approved.has(resource.id) || !resource.title ||
        resource.kind !== (resource.id === "graph-pilot-channel" ? "channel" : "chat")) ||
      metadata.resources.filter(resource => resource.id === "graph-pilot-chat" || resource.id === "graph-pilot-channel")
        .some(resource => !resource.title.startsWith("Teams Reference Validation")))
    throw new SignInCheckError("The private Live inventory differs from the exact existing dedicated resources and approved pairs.");
}

/** @param {LiveMetadata} metadata @param {string} account */
export function applyLiveSettings(metadata, account) {
  validateLiveMetadata(metadata);
  const actor = metadata?.actors?.find(value => value.persona === account);
  if (!actor) throw new SignInCheckError("The requested validation account is not approved.");
  process.env.REFERENCE_BASE_URL ??= metadata.origin;
  process.env.ENTRA_TENANT_ID ??= metadata.tenantId;
  process.env.ENTRA_CLIENT_ID ??= metadata.webClientId;
  process.env[`TEST_${account.toUpperCase()}_OBJECT_ID`] ??= actor.id;
  process.env[`TEST_${account.toUpperCase()}_EXPECTED_USERNAME`] ??= actor.upn;
  process.env.ENTRA_AUTH_PROFILE ??= metadata.authProfile ?? "live-delegated";
}

/**
 * Only reads the original operation; never dispatches or retries a Teams write.
 * @param {import("@playwright/test").APIRequestContext} request
 * @param {string} origin
 * @param {string} conversationId
 * @param {import("../src/api.js").Operation} original
 * @param {number} [timeoutMs]
 */
export async function waitForOriginalOperation(request, origin, conversationId, original, timeoutMs = 30_000) {
  if (!original || typeof original.id !== "string" || !original.id)
    throw new SignInCheckError("The send returned no original operation ID; no resend was attempted.");
  const deadline = Date.now() + timeoutMs;
  let operation = original;
  while (true) {
    if (operation.id !== original.id)
      throw new SignInCheckError("The operation check returned a different ID; no resend was attempted.");
    if (operation.state === "confirmed" && typeof operation.messageId === "string" && operation.messageId)
      return { ...operation, messageId: operation.messageId };
    if (!["pending", "dispatching", "unknown"].includes(operation.state) || Date.now() >= deadline)
      throw new SignInCheckError("The original send was not definitively confirmed. Inspect it before any further send; no resend was attempted.");
    await new Promise(resolve => setTimeout(resolve, Math.min(1000, Math.max(1, deadline - Date.now()))));
    const response = await request.get(`${origin}/api/conversations/${encodeURIComponent(conversationId)}/operations/${encodeURIComponent(original.id)}`,
      { maxRedirects: 0, timeout: Math.max(1, Math.min(5000, deadline - Date.now())) });
    try {
      if (response.status() !== 200)
        throw new SignInCheckError("The original operation could not be verified; no resend was attempted.");
      operation = await response.json();
    } finally { await response.dispose(); }
  }
}

/**
 * @param {import("@playwright/test").BrowserContext} context
 * @param {import("@playwright/test").Page} page
 * @param {ReturnType<import("./entra-signin.mjs").readSettings>} settings
 * @param {LiveMetadata} metadata
 * @param {string} settingsPath
 * @param {(value: string) => void} stage
 */
export async function validateTeams(context, page, settings, metadata, settingsPath, stage) {
  validateLiveMetadata(metadata);
  if (settings.authProfile !== (metadata.authProfile ?? "live-delegated") || metadata.origin !== settings.origin ||
      metadata.tenantId !== settings.tenantId || metadata.webClientId !== settings.clientId)
    throw new SignInCheckError("Teams validation settings differ from the pinned authentication boundary.");
  const sessionResponse = await context.request.get(`${settings.origin}/api/session`, { maxRedirects: 0 });
  const session = await sessionResponse.json();
  if (sessionResponse.status() !== 200 || session.id !== settings.actorId || !isLiveSession(session) ||
      (settings.authProfile === "service" ? !isServiceSession(session) : session.scopedValidation !== true))
    throw new SignInCheckError("The session is not the approved event-driven Live workspace.");
  await context.unroute(`${settings.origin}/api/**`);
  stage("open the real Teams-backed workspace");
  await page.goto(settings.origin, { waitUntil: "domcontentloaded" });
  await expect(page.getByText("Development · Microsoft Teams", { exact: true })).toBeVisible({ timeout: 30000 });
  const navigation = page.getByRole("navigation", { name: "Conversations", exact: true });
  const catalogResponse = await context.request.get(`${settings.origin}/api/conversations`, { maxRedirects: 0, timeout: 30000 });
  const catalog = await catalogResponse.json();
  const pairs = metadata.actors.filter(actor => actor.id !== settings.actorId).map(actor => pairId(settings.actorId, actor.id));
  const shared = metadata.resources.filter(resource => ["graph-pilot-chat", "graph-pilot-channel"].includes(resource.id));
  if (catalogResponse.status() !== 200 || !Array.isArray(catalog) || catalog.length < 2 || catalog.length > 2 + pairs.length ||
      shared.some(resource => !catalog.some(item => item.id === resource.id)) ||
      catalog.some(item => !shared.some(resource => resource.id === item.id) && !pairs.includes(item.id)))
    throw new SignInCheckError("Teams did not authorize both dedicated validation resources.");
  const chat = shared.find(resource => resource.kind === "chat");
  const channel = shared.find(resource => resource.kind === "channel");
  if (!chat || !channel) throw new SignInCheckError("The approved chat/channel pair is missing.");
  const run = `${settings.account}-${Date.now()}`;
  /** @type {{account:string,actorId:string,source:string,readChat:boolean,readChannel:boolean,chatMessageId?:string,channelMessageId?:string,replyMessageId?:string}} */
  const proof = { account: settings.account, actorId: settings.actorId, source: "Microsoft Teams via Live projection and delegated sends", readChat: false, readChannel: false };
  let lastSendCompleted = 0;
  /** @param {LiveResource} resource @param {string | null} parent */
  const read = async (resource, parent = null) => {
    const response = await context.request.get(`${settings.origin}/api/conversations/${resource.id}/messages` +
      (parent ? `?parent=${encodeURIComponent(parent)}` : ""), { maxRedirects: 0, timeout: 30000 });
    /** @type {{messages:GraphMessage[]}} */
    const value = await response.json();
    if (response.status() !== 200 || !Array.isArray(value.messages))
      throw new SignInCheckError("Real Teams messages could not be read through the application.");
    return value;
  };
  /** @param {LiveResource} resource @param {string} body @param {string | null} parent @returns {Promise<string>} */
  const sendInUi = async (resource, body, parent = null) => {
    const remainingBudget = 1400 - (Date.now() - lastSendCompleted);
    if (remainingBudget > 0) await new Promise(resolve => setTimeout(resolve, remainingBudget));
    const endpoint = `/api/conversations/${resource.id}/messages`;
    const responsePromise = page.waitForResponse(response =>
      new URL(response.url()).pathname === endpoint && response.request().method() === "POST", { timeout: 45000 });
    await page.getByRole("textbox", { name: parent ? "Write a reply" : "Write a message", exact: true }).fill(body);
    await page.getByRole("button", { name: parent ? "Reply" : "Send", exact: true }).click({ timeout: 30000 });
    const response = await responsePromise;
    if (![201, 202].includes(response.status()))
      throw new SignInCheckError("The application did not accept the original send; no resend was attempted.");
    const operation = await waitForOriginalOperation(context.request, settings.origin, resource.id, await response.json());
    lastSendCompleted = Date.now();
    for (let attempt = 0; attempt < 6; attempt++) {
      const value = await read(resource, parent);
      const message = value.messages.find(item => item.id === operation.messageId);
      if (message) {
        if (message.body !== body || message.actorId !== settings.actorId)
          throw new SignInCheckError("The projected Teams message body or author did not match the authenticated sender.");
        return operation.messageId;
      }
      await new Promise(resolve => setTimeout(resolve, 2000));
    }
    throw new SignInCheckError("Creation was confirmed but projected read-back was not observed; no duplicate POST was attempted.");
  };
  stage("read the dedicated Teams chat");
  await read(chat); proof.readChat = true;
  await navigation.getByRole("button").filter({ hasText: chat.title }).click();
  stage("send and verify the real chat message");
  proof.chatMessageId = await sendInUi(chat, `Synthetic Teams validation ${run}: chat message.`);
  stage("read the dedicated Teams channel");
  await read(channel); proof.readChannel = true;
  await navigation.getByRole("button").filter({ hasText: channel.title }).click();
  stage("post and verify the real channel message");
  proof.channelMessageId = await sendInUi(channel, `Synthetic Teams validation ${run}: channel post.`);
  await page.reload({ waitUntil: "domcontentloaded" });
  await navigation.getByRole("button").filter({ hasText: channel.title }).click({ timeout: 30000 });
  const parent = page.locator(`[data-message-id=${JSON.stringify(proof.channelMessageId)}]`);
  await parent.getByRole("button", { name: "Open thread", exact: true }).click({ timeout: 30000 });
  stage("reply and verify the real channel thread");
  proof.replyMessageId = await sendInUi(channel, `Synthetic Teams validation ${run}: thread reply.`, proof.channelMessageId);
  const output = join(dirname(settingsPath), "validation-results");
  mkdirSync(output, { recursive: true });
  stage("capture the authenticated application evidence");
  await page.screenshot({ path: join(output, `${settings.account}-teams.png`), fullPage: true });
  writeFileSync(join(output, `${settings.account}.json`), JSON.stringify({ ...proof, completedAt: new Date().toISOString() }, null, 2));
  return proof;
}

/**
 * Exactly one separately approved synthetic send, never the three-message --teams suite.
 * @param {import("@playwright/test").BrowserContext} context
 * @param {import("@playwright/test").Page} page
 * @param {ReturnType<import("./entra-signin.mjs").readSettings>} settings
 * @param {LiveMetadata} metadata
 * @param {string} settingsPath
 * @param {(value:string)=>void} stage
 */
export async function validateDirectChat(context, page, settings, metadata, settingsPath, stage) {
  validateLiveMetadata(metadata);
  const target = metadata.actors.find(actor => actor.persona === "alex");
  if (settings.account !== "jordan" || settings.authProfile !== (metadata.authProfile ?? "live-delegated") || !metadata.directChatsEnabled ||
      metadata.origin !== settings.origin || metadata.tenantId !== settings.tenantId ||
      metadata.webClientId !== settings.clientId || !target || target.id === settings.actorId)
    throw new SignInCheckError("Direct-chat validation differs from the approved Jordan-to-Alex identity/resource scope.");
  const sessionResponse = await context.request.get(`${settings.origin}/api/session`, { maxRedirects: 0 });
  const session = await sessionResponse.json();
  if (sessionResponse.status() !== 200 || session.id !== settings.actorId || !isLiveSession(session) ||
      (settings.authProfile === "service" ? !isServiceSession(session) : session.scopedValidation !== true) ||
      session.canStartDirectChats !== true)
    throw new SignInCheckError("The session cannot reuse the approved Live direct chat without creating a new chat.");
  const pair = pairId(settings.actorId, target.id);
  const messagePath = `/api/conversations/${pair}/messages`;
  const body = "UI validation: Jordan to Alex";
  let messagePosts = 0, chatOpens = 0, unexpectedWrite = false;
  await context.unroute(`${settings.origin}/api/**`);
  await context.route(`${settings.origin}/api/**`, async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "GET") return route.continue();
    if (request.method() === "POST" && path === "/api/realtime/negotiate") return route.continue();
    const data = request.postDataJSON();
    if (request.method() === "POST" && path === "/api/chats/direct" && data?.personId === target.id && chatOpens === 0) {
      chatOpens++; return route.continue();
    }
    if (request.method() === "POST" && path === messagePath && data?.body === body &&
        data?.parentId === null && typeof data?.operationId === "string" && messagePosts === 0) {
      messagePosts++; return route.continue();
    }
    unexpectedWrite = true;
    await route.abort("blockedbyclient");
  });
  stage("find Alex by identity in the real Jordan people view");
  await page.goto(settings.origin, { waitUntil: "domcontentloaded" });
  const peopleResponse = await context.request.get(`${settings.origin}/api/people`, { maxRedirects: 0, timeout: 30000 });
  const people = await peopleResponse.json();
  if (peopleResponse.status() !== 200 || !Array.isArray(people) || people.length > 2 ||
      people.some(person => person.id === settings.actorId || !metadata.actors.some(actor => actor.id === person.id)))
    throw new SignInCheckError("People discovery exceeded the other two approved test identities.");
  const alex = people.find(person => person.id === target.id);
  if (!alex?.name || !alex?.email) throw new SignInCheckError("Alex's verified name/email were unavailable.");
  const search = page.getByRole("searchbox", { name: "To", exact: true });
  await search.fill("alex");
  const recipient = page.getByRole("button", { name: `Choose: ${alex.name}, ${alex.email}`, exact: true });
  await expect(recipient).toBeVisible({ timeout: 30000 });
  await search.fill(alex.email);
  await expect(recipient).toBeVisible();
  stage("open the real exact-pair one-to-one chat without sending");
  const opening = page.waitForResponse(response => new URL(response.url()).pathname === "/api/chats/direct" &&
    response.request().method() === "POST", { timeout: 45000 });
  await recipient.click();
  const openedResponse = await opening;
  const opened = await openedResponse.json();
  if (openedResponse.status() !== 200 || opened.id !== pair || opened.kind !== "chat" || opened.title !== alex.name || messagePosts)
    throw new SignInCheckError("The selected person did not open the expected authorized pair, or selection attempted a send.");
  const composer = page.getByRole("textbox", { name: "Write a message", exact: true });
  await expect(composer).toBeFocused();
  await expect(page.getByRole("heading", { name: alex.name, exact: true })).toBeVisible();
  stage("send the one approved synthetic Jordan-to-Alex message");
  await composer.fill(body);
  const posted = page.waitForResponse(response => new URL(response.url()).pathname === messagePath &&
    response.request().method() === "POST", { timeout: 45000 });
  await page.getByRole("button", { name: "Send", exact: true }).click();
  const response = await posted;
  if (![201, 202].includes(response.status()))
    throw new SignInCheckError("The approved send was not accepted; no retry was attempted.");
  const operation = await waitForOriginalOperation(context.request, settings.origin, pair, await response.json());
  stage("read back the exact Graph message ID, Jordan author and approved text");
  let matched = false;
  for (let attempt = 0; attempt < 6; attempt++) {
    const read = await context.request.get(`${settings.origin}${messagePath}`, { maxRedirects: 0, timeout: 30000 });
    if (read.status() === 200) {
      const value = await read.json();
      const message = value.messages?.find((/** @type {GraphMessage} */ item) => item.id === operation.messageId);
      if (message) {
        if (message.actorId !== settings.actorId || message.body !== body)
          throw new SignInCheckError("The Graph message ID did not match the approved sender/text.");
        matched = true; break;
      }
    } else if ([401, 403, 404].includes(read.status()))
      throw new SignInCheckError("The direct message read-back was not authorized.");
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
  if (!matched) throw new SignInCheckError("Graph creation was confirmed but read-back was not observed; no duplicate POST was attempted.");
  stage("reopen the admitted pair from the real conversation list");
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "Conversations", exact: true }).getByRole("button").filter({ hasText: alex.name }).click({ timeout: 30000 });
  await expect(page.locator(".message-text").filter({ hasText: body })).toHaveCount(1, { timeout: 30000 });
  if (messagePosts !== 1 || chatOpens !== 1 || unexpectedWrite)
    throw new SignInCheckError("The browser exceeded the exactly approved chat-reuse/send boundary.");
  const proof = { account: "jordan", recipient: "alex", source: "real delegated Microsoft Graph",
    nameAndEmailSearch: true, exactPairOpened: true, selectionSentNothing: true,
    messagePosts, graphCreationConfirmed: true, exactIdAuthorBodyReadBack: true, reopenedWithoutDuplicate: true };
  const output = join(dirname(settingsPath), "validation-results");
  mkdirSync(output, { recursive: true });
  writeFileSync(join(output, "jordan-to-alex.json"), JSON.stringify({
    ...proof, pairId: pair, graphMessageId: operation.messageId, operationId: operation.id, completedAt: new Date().toISOString(),
  }, null, 2));
  return proof;
}
