import { expect } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { SignInCheckError } from "./entra-signin.mjs";

/** @typedef {{ id: string, kind: "chat" | "channel", title: string }} PilotResource */
/** @typedef {{ schemaVersion: number, tenantId: string, webClientId: string, origin: string, actors: {persona:string,id:string,upn:string}[], resources: PilotResource[] }} PilotMetadata */
/** @typedef {{ id: string, body: string, actorId: string }} GraphMessage */

/** @param {PilotMetadata} metadata @param {string} account */
export function applyPilotSettings(metadata, account) {
  const actor = metadata?.actors?.find(value => value.persona === account);
  if (metadata?.schemaVersion !== 1 || metadata.actors.length !== 3 || metadata.resources?.length !== 2 ||
      !actor?.id || !actor?.upn || !metadata.origin?.startsWith("https://") ||
      metadata.resources.some(value => !value.id?.startsWith("graph-pilot-") ||
        !value.title?.startsWith("Teams Reference Validation"))) {
    throw new SignInCheckError("The private Teams validation metadata is not the approved three-user/two-resource scope.");
  }
  process.env.REFERENCE_BASE_URL ??= metadata.origin;
  process.env.ENTRA_TENANT_ID ??= metadata.tenantId;
  process.env.ENTRA_CLIENT_ID ??= metadata.webClientId;
  process.env[`TEST_${account.toUpperCase()}_OBJECT_ID`] ??= actor.id;
  process.env[`TEST_${account.toUpperCase()}_EXPECTED_USERNAME`] ??= actor.upn;
  process.env.ENTRA_AUTH_PROFILE ??= "graph-pilot";
}

/**
 * @param {import("@playwright/test").BrowserContext} context
 * @param {import("@playwright/test").Page} page
 * @param {ReturnType<import("./entra-signin.mjs").readSettings>} settings
 * @param {PilotMetadata} metadata
 * @param {string} settingsPath
 * @param {(value: string) => void} stage
 */
export async function validateTeams(context, page, settings, metadata, settingsPath, stage) {
  if (settings.authProfile !== "graph-pilot" || metadata.origin !== settings.origin ||
      metadata.tenantId !== settings.tenantId || metadata.webClientId !== settings.clientId)
    throw new SignInCheckError("Teams validation settings differ from the pinned authentication boundary.");
  const sessionResponse = await context.request.get(`${settings.origin}/api/session`, { maxRedirects: 0 });
  const session = await sessionResponse.json();
  if (sessionResponse.status() !== 200 || session.id !== settings.actorId || session.simulated !== false ||
      session.localAuth !== false || session.basicValidation !== true)
    throw new SignInCheckError("The session is not the real-Teams basic validation mode.");
  await context.unroute(`${settings.origin}/api/**`);
  stage("open the real Teams-backed workspace");
  await page.goto(settings.origin, { waitUntil: "domcontentloaded" });
  await expect(page.getByText("Basic Teams validation", { exact: true })).toBeVisible({ timeout: 30000 });
  const navigation = page.getByRole("navigation", { name: "Conversations", exact: true });
  const catalogResponse = await context.request.get(`${settings.origin}/api/conversations`, { maxRedirects: 0, timeout: 30000 });
  const catalog = await catalogResponse.json();
  if (catalogResponse.status() !== 200 || !Array.isArray(catalog) || catalog.length !== 2 ||
      catalog.some(item => !metadata.resources.some(resource => resource.id === item.id)))
    throw new SignInCheckError("Teams did not authorize both dedicated validation resources.");
  const chat = metadata.resources.find(resource => resource.kind === "chat");
  const channel = metadata.resources.find(resource => resource.kind === "channel");
  if (!chat || !channel) throw new SignInCheckError("The approved chat/channel pair is missing.");
  const run = `${settings.account}-${Date.now()}`;
  /** @type {{account:string,actorId:string,source:string,readChat:boolean,readChannel:boolean,chatMessageId?:string,channelMessageId?:string,replyMessageId?:string}} */
  const proof = { account: settings.account, actorId: settings.actorId, source: "Microsoft Teams via delegated Graph", readChat: false, readChannel: false };
  /** @param {PilotResource} resource @param {string | null} parent */
  const read = async (resource, parent = null) => {
    const response = await context.request.get(`${settings.origin}/api/conversations/${resource.id}/messages` +
      (parent ? `?parent=${encodeURIComponent(parent)}` : ""), { maxRedirects: 0, timeout: 30000 });
    /** @type {{simulated:boolean,messages:GraphMessage[]}} */
    const value = await response.json();
    if (response.status() !== 200 || value.simulated !== false || !Array.isArray(value.messages))
      throw new SignInCheckError("Real Teams messages could not be read through the application.");
    return value;
  };
  /** @param {PilotResource} resource @param {string} body @param {string | null} parent @returns {Promise<string>} */
  const sendInUi = async (resource, body, parent = null) => {
    const endpoint = `/api/conversations/${resource.id}/messages`;
    const responsePromise = page.waitForResponse(response =>
      new URL(response.url()).pathname === endpoint && response.request().method() === "POST", { timeout: 45000 });
    await page.getByRole("textbox", { name: parent ? "Write a reply" : "Write a message", exact: true }).fill(body);
    await page.getByRole("button", { name: parent ? "Reply" : "Send", exact: true }).click({ timeout: 30000 });
    const response = await responsePromise;
    const operation = await response.json();
    if (response.status() !== 201 || operation.state !== "confirmed" || operation.simulated !== false ||
        typeof operation.messageId !== "string" || !operation.messageId)
      throw new SignInCheckError("The application did not return definitive real Graph creation evidence; no resend was attempted.");
    for (let attempt = 0; attempt < 6; attempt++) {
      const value = await read(resource, parent);
      const message = value.messages.find(item => item.id === operation.messageId);
      if (message) {
        if (message.body !== body || message.actorId !== settings.actorId)
          throw new SignInCheckError("The Graph-read message body or author did not match the authenticated sender.");
        return operation.messageId;
      }
      await new Promise(resolve => setTimeout(resolve, 2000));
    }
    throw new SignInCheckError("Creation was confirmed but Graph read-back was not observed; no duplicate POST was attempted.");
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
