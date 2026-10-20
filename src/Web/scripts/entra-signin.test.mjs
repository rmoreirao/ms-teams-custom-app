import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { test } from "node:test";
import { chromium } from "@playwright/test";
import { credentialKeys, describeEndpoint, generateTotp, isLiveSession, readPageState, readSettings, SignInCheckError, validatePasswordEntry, verifyAuthorization, waitForSession } from "./entra-signin.mjs";
import { validateLiveMetadata, waitForOriginalOperation } from "./teams-validation.mjs";
import { createHash } from "node:crypto";

const seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
const env = {
  REFERENCE_BASE_URL: "https://dev.example.test",
  ENTRA_TENANT_ID: "11111111-1111-1111-1111-111111111111",
  ENTRA_CLIENT_ID: "22222222-2222-2222-2222-222222222222",
  ENTRA_EXPECTED_ACTOR_ID: "33333333-3333-3333-3333-333333333333",
  ENTRA_EXPECTED_USERNAME: "alex@example.test",
  TEST_ALEX_USER: "alex@example.test",
  TEST_ALEX_PASSWORD: "unit-test-password",
  TEST_ALEX_TOTP_SECRET: seed,
};

test("verifies the expected session independently of a closed page and rejects invalid sessions", async () => {
  const actorId = env.ENTRA_EXPECTED_ACTOR_ID;
  let status = 200;
  let actor = actorId;
  let source = "Microsoft Teams";
  let legacy = false;
  const server = createServer((incoming, response) => {
    assert.equal(incoming.method, "GET");
    assert.equal(incoming.url, "/api/session");
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ id: actor, environment: "Development", source, scopedValidation: true,
      canStartDirectChats: true, canCreateDirectChats: false, ...(legacy ? { localAuth: false } : {}) }));
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Local mock server has no TCP address.");
  const origin = `http://127.0.0.1:${address.port}`;
  /** @type {import("@playwright/test").Browser | undefined} */
  let browser;
  try {
    browser = await chromium.launch({
      headless: true,
      ...(process.platform === "win32" ? { channel: "msedge" } : {}),
    });
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.close();
    assert.deepEqual(await waitForSession(context.request, { origin, actorId }), { environment: "Development", source: "Microsoft Teams" });
    actor = "unexpected-actor";
    await assert.rejects(waitForSession(context.request, { origin, actorId }), SignInCheckError);
    actor = actorId;
    source = "unexpected";
    await assert.rejects(waitForSession(context.request, { origin, actorId }), SignInCheckError);
    source = "Microsoft Teams";
    legacy = true;
    await assert.rejects(waitForSession(context.request, { origin, actorId }), SignInCheckError);
    legacy = false;
    for (status of [302, 403, 503]) {
      await assert.rejects(waitForSession(context.request, { origin, actorId }), SignInCheckError);
    }
    status = 401;
    await assert.rejects(waitForSession(context.request, { origin, actorId }, 100), SignInCheckError);
  } finally {
    await browser?.close();
    server.close();
    await once(server, "close");
  }
});

test("distinguishes the persistence prompt from MFA checkboxes and Cancel controls without network access", async () => {
  const browser = await chromium.launch({
    headless: true,
    ...(process.platform === "win32" ? { channel: "msedge" } : {}),
  });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    let html = "";
    await context.route("**/*", route => route.fulfill({ contentType: "text/html", body: html }));
    const input = {
      origin: env.REFERENCE_BASE_URL,
      used: { accountPicker: false, username: true, password: true, alternate: true, otpOption: true, otp: true, staySignedIn: false },
    };
    html = '<h1>Enter a verification code</h1><input id="KmsiCheckboxField" type="checkbox"><button id="idBtn_Back">Cancel</button>';
    await page.goto("https://login.microsoftonline.com/test");
    assert.notEqual(await page.evaluate(readPageState, input), "stay-signed-in");
    html = '<h1>Stay signed in?</h1><button>No</button><button>Yes</button>';
    await page.goto("https://login.microsoftonline.com/test");
    assert.equal(await page.evaluate(readPageState, input), "stay-signed-in");
    html = '<h1 hidden>Stay signed in?</h1><button>No</button>';
    await page.goto("https://login.microsoftonline.com/test");
    assert.notEqual(await page.evaluate(readPageState, input), "stay-signed-in");
    html = '<h1>Sign in</h1><input name="loginfmt">';
    await page.goto("https://login.microsoftonline.com/test");
    assert.equal(await page.evaluate(readPageState, input), "unexpected-restart");
  } finally {
    await browser.close();
  }
});

test("diagnostic endpoints never reveal URLs, query strings, fragments or unknown paths", () => {
  assert.equal(describeEndpoint("https://dev.example.test/auth/callback?code=private#state", env.REFERENCE_BASE_URL), "app-callback");
  assert.equal(describeEndpoint("https://login.microsoftonline.com/kmsi?state=private", env.REFERENCE_BASE_URL), "entra-persistence");
  assert.equal(describeEndpoint("https://login.microsoftonline.com/unknown/private", env.REFERENCE_BASE_URL), "entra-other");
  assert.equal(describeEndpoint("https://unexpected.example.test/private", env.REFERENCE_BASE_URL), "other-site");
});

test("defers only the explicitly offered registration campaign after password/TOTP and opt-in", async () => {
  const browser = await chromium.launch({ headless: true, ...(process.platform === "win32" ? { channel: "msedge" } : {}) });
  try {
    const page = await browser.newPage();
    let html = '<h1>Let&#39;s keep your account secure</h1><a href="#">Not now</a><button>Next</button>';
    await page.route("**/*", route => route.fulfill({ contentType: "text/html", body: html }));
    const input = {
      origin: env.REFERENCE_BASE_URL,
      used: { accountPicker: false, username: true, password: true, alternate: true, otpOption: true, otp: true,
        staySignedIn: false, allowRegistrationDeferral: true, registrationDeferred: false },
    };
    await page.goto("https://login.microsoftonline.com/test");
    assert.equal(await page.evaluate(readPageState, input), "optional-registration");
    for (const used of [
      { ...input.used, allowRegistrationDeferral: false },
      { ...input.used, otp: false },
      { ...input.used, password: false },
      { ...input.used, registrationDeferred: true },
    ]) assert.equal(await page.evaluate(readPageState, { ...input, used }), "setup");
    html = '<h1>Let&#39;s keep your account secure</h1><button>Next</button>';
    await page.goto("https://login.microsoftonline.com/test");
    assert.equal(await page.evaluate(readPageState, input), "setup");
    html = '<h1>Add a sign-in method</h1><button>Passkey</button><a>Not now</a>';
    await page.goto("https://login.microsoftonline.com/test");
    assert.equal(await page.evaluate(readPageState, input), "setup");
    html = '<h1>Let&#39;s keep your account secure</h1><a>Not now</a><input id="idTxtBx_SAOTCC_OTC"><div id="idSpan_SAOTCC_ErrorMsg_Identity">Incorrect code</div>';
    await page.goto("https://login.microsoftonline.com/test");
    assert.equal(await page.evaluate(readPageState, input), "rejected-mfa");
  } finally { await browser.close(); }
  assert.equal(readSettings({ ...env, ENTRA_AUTH_PROFILE: "live", ENTRA_DEFER_OPTIONAL_REGISTRATION: "true" }).allowRegistrationDeferral, true);
  assert.throws(() => readSettings({ ...env, ENTRA_DEFER_OPTIONAL_REGISTRATION: "true" }), SignInCheckError);
});

test("rejects password truncation and ambiguous duplicate entries without exposing values", () => {
  assert.throws(() => validatePasswordEntry("TEST_ALEX_PASSWORD=example#suffix"), SignInCheckError);
  assert.throws(() => validatePasswordEntry("TEST_ALEX_PASSWORD=one\nTEST_ALEX_PASSWORD=two"), SignInCheckError);
  for (const text of [
    'TEST_ALEX_PASSWORD="example#suffix"',
    "TEST_ALEX_PASSWORD='example#suffix'",
    "TEST_ALEX_PASSWORD=example",
    "# TEST_ALEX_PASSWORD=not-an-entry",
    "REFERENCE_BASE_URL=https://dev.example.test",
  ]) {
    assert.doesNotThrow(() => validatePasswordEntry(text));
  }
});

test("validates the expected fixture and normalizes a copied Base32 seed", () => {
  const settings = readSettings({ ...env, TEST_ALEX_TOTP_SECRET: seed.toLowerCase().replace(/.{8}/g, "$& ") });
  assert.equal(settings.seed, seed);
  assert.equal(settings.origin, env.REFERENCE_BASE_URL);
});

test("selects only the explicitly supported test browsers", () => {
  assert.equal(readSettings(env).browserChannel, "chromium");
  assert.equal(readSettings({ ...env, ENTRA_TEST_BROWSER: "chromium" }).browserChannel, "chromium");
  assert.equal(readSettings({ ...env, ENTRA_TEST_BROWSER: "msedge" }).browserChannel, "msedge");
  assert.throws(() => readSettings({ ...env, ENTRA_TEST_BROWSER: "unsupported" }), SignInCheckError);
});

test("isolates all three named fixture credentials without account fallback", () => {
  assert.equal(readSettings(env).account, "alex");
  for (const account of ["alex", "sam", "jordan"]) {
    const keys = credentialKeys(account);
    const username = `${account}@example.test`;
    const selected = {
      ...env,
      ENTRA_EXPECTED_USERNAME: username,
      [keys.username]: username,
      [keys.password]: "named-fixture-password",
      [keys.seed]: seed,
    };
    const settings = readSettings(selected, account);
    assert.equal(settings.username, username);
    assert.equal(settings.password, "named-fixture-password");
    assert.equal(settings.account, account);
    assert.throws(() => readSettings({ ...selected, [keys.password]: "" }, account), SignInCheckError);
    assert.throws(() => readSettings({ ...selected, ENTRA_EXPECTED_USERNAME: "unexpected@example.test" }, account), SignInCheckError);
    assert.throws(() => validatePasswordEntry(`${keys.password}=example#suffix`, keys.password), SignInCheckError);
    assert.throws(() => validatePasswordEntry(`${keys.password}=one\n${keys.password}=two`, keys.password), SignInCheckError);
    assert.doesNotThrow(() => validatePasswordEntry(`${keys.password}="example#suffix"`, keys.password));
  }
  assert.throws(() => credentialKeys("unknown"), SignInCheckError);
});

test("rejects absent inputs, wrong identities, unsafe targets and credential logging", () => {
  for (const key of Object.keys(env)) {
    assert.throws(() => readSettings({ ...env, [key]: "" }), SignInCheckError);
  }
  for (const override of [
    { TEST_ALEX_USER: "admin@example.test" },
    { TEST_ALEX_TOTP_SECRET: "123456" },
    { REFERENCE_BASE_URL: "http://dev.example.test" },
    { REFERENCE_BASE_URL: "https://user:password@dev.example.test" },
    { REFERENCE_BASE_URL: "https://dev.example.test/path" },
    { ENTRA_TENANT_ID: "not-a-guid" },
    { DEBUG: "pw:api" },
    { PWDEBUG: "1" },
  ]) {
    assert.throws(() => readSettings({ ...env, ...override }), SignInCheckError);
  }
});

test("pins authorization to the expected Entra tenant, app, callback and sign-in-only scopes", () => {
  const settings = readSettings(env);
  const url = new URL(`https://login.microsoftonline.com/${settings.tenantId}/oauth2/v2.0/authorize`);
  url.searchParams.set("client_id", settings.clientId);
  url.searchParams.set("redirect_uri", `${settings.origin}/auth/callback`);
  url.searchParams.set("scope", "openid profile offline_access");
  assert.equal(verifyAuthorization(url.href, settings), url.href);
  assert.throws(() => verifyAuthorization(undefined, settings), SignInCheckError);
  for (const [name, value] of [
    ["client_id", env.ENTRA_TENANT_ID],
    ["redirect_uri", "https://unexpected.example.test/auth/callback"],
    ["scope", "openid profile offline_access ChatMessage.Send"],
  ]) {
    const changed = new URL(url);
    changed.searchParams.set(name, value);
    assert.throws(() => verifyAuthorization(changed.href, settings), SignInCheckError);
  }
  const wrongHost = new URL(url);
  wrongHost.hostname = "unexpected.example.test";
  assert.throws(() => verifyAuthorization(wrongHost.href, settings), SignInCheckError);
  const withCredentials = new URL(url);
  withCredentials.username = "unexpected";
  assert.throws(() => verifyAuthorization(withCredentials.href, settings), SignInCheckError);
  const wrongTenant = new URL(url);
  wrongTenant.pathname = "/common/oauth2/v2.0/authorize";
  assert.throws(() => verifyAuthorization(wrongTenant.href, settings), SignInCheckError);
});

test("uses per-account object IDs and the exact Live Graph scope profile", () => {
  const settings = readSettings({
    ...env, ENTRA_AUTH_PROFILE: "live",
    TEST_ALEX_OBJECT_ID: env.ENTRA_EXPECTED_ACTOR_ID,
    TEST_ALEX_EXPECTED_USERNAME: env.TEST_ALEX_USER,
    ENTRA_EXPECTED_ACTOR_ID: "", ENTRA_EXPECTED_USERNAME: "",
  });
  const url = new URL(`https://login.microsoftonline.com/${settings.tenantId}/oauth2/v2.0/authorize`);
  url.searchParams.set("client_id", settings.clientId);
  url.searchParams.set("redirect_uri", `${settings.origin}/auth/callback`);
  url.searchParams.set("scope", "openid profile offline_access https://graph.microsoft.com/Chat.Read ChatMessage.Send Channel.ReadBasic.All ChannelMessage.Read.All ChannelMessage.Send");
  assert.equal(verifyAuthorization(url.href, settings), url.href);
  url.searchParams.set("scope", `${url.searchParams.get("scope")} Directory.ReadWrite.All`);
  assert.throws(() => verifyAuthorization(url.href, settings), SignInCheckError);
});

test("matches the six-digit SHA1/30-second forms of RFC 6238 test vectors", () => {
  for (const [seconds, expected] of [
    [59, "287082"],
    [1111111109, "081804"],
    [1111111111, "050471"],
    [1234567890, "005924"],
    [2000000000, "279037"],
    [20000000000, "353130"],
  ]) {
    assert.equal(generateTotp(seed, Number(seconds) * 1000), expected);
  }
});

test("preserves the exact existing delegated Live consent including Chat.Create without widening it", () => {
  const settings = readSettings({ ...env, ENTRA_AUTH_PROFILE: "live-delegated" });
  const url = new URL(`https://login.microsoftonline.com/${settings.tenantId}/oauth2/v2.0/authorize`);
  url.searchParams.set("client_id", settings.clientId);
  url.searchParams.set("redirect_uri", `${settings.origin}/auth/callback`);
  url.searchParams.set("scope", "openid profile offline_access Chat.Read ChatMessage.Send Channel.ReadBasic.All ChannelMessage.Read.All ChannelMessage.Send Chat.Create");
  assert.equal(verifyAuthorization(url.href, settings), url.href);
  assert.throws(() => verifyAuthorization(url.href, { ...settings, authProfile: "live" }), SignInCheckError);
  url.searchParams.set("scope", url.searchParams.get("scope") + " User.ReadBasic.All");
  assert.throws(() => verifyAuthorization(url.href, settings), SignInCheckError);
});

test("historical profile aliases normalize only to the same exact Live consent sets", () => {
  for (const [alias, canonical] of [["graph-pilot", "live"], ["graph-pilot-direct", "live-delegated"]]) {
    assert.equal(readSettings({ ...env, ENTRA_AUTH_PROFILE: alias }).authProfile, canonical);
  }
  assert.throws(() => readSettings({ ...env, ENTRA_AUTH_PROFILE: "broad-graph" }), SignInCheckError);
});

test("Live contract rejects retired fields, incorrect source, environment and chat-creation capability", () => {
  const session = { environment: "Development", source: "Microsoft Teams", scopedValidation: true,
    canStartDirectChats: true, canCreateDirectChats: false };
  assert.equal(isLiveSession(session), true);
  for (const patch of [
    { simulated: false }, { localAuth: false }, { scenarioAdmin: false }, { basicValidation: false },
    { environment: "Production" }, { source: "fixture" }, { canCreateDirectChats: true },
    { canStartDirectChats: undefined }, { scopedValidation: undefined },
  ]) assert.equal(isLiveSession({ ...session, ...patch }), false);
});

test("Live metadata pins the same three actors and all five durable resources", () => {
  const actors = ["alex", "sam", "jordan"].map((persona, index) => ({
    persona, id: `${index + 3}`.repeat(8) + "-" + `${index + 3}`.repeat(4) + "-" +
      `${index + 3}`.repeat(4) + "-" + `${index + 3}`.repeat(4) + "-" + `${index + 3}`.repeat(12),
    upn: `${persona}@example.test`,
  }));
  const resources = [
    { id: "graph-pilot-chat", kind: "chat", title: "Teams Reference Validation chat" },
    { id: "graph-pilot-channel", kind: "channel", title: "Teams Reference Validation channel" },
    ...actors.flatMap((actor, index) => actors.slice(index + 1).map(other => ({
      id: "graph-pilot-dm-" + createHash("sha256").update([actor.id, other.id].sort().join(":")).digest("hex"),
      kind: "chat", title: `${actor.persona} and ${other.persona}`,
    }))),
  ];
  /** @type {import("./teams-validation.mjs").LiveMetadata} */
  const metadata = { schemaVersion: 2, tenantId: env.ENTRA_TENANT_ID, webClientId: env.ENTRA_CLIENT_ID,
    origin: env.REFERENCE_BASE_URL, actors, resources: /** @type {import("./teams-validation.mjs").LiveResource[]} */ (resources) };
  assert.doesNotThrow(() => validateLiveMetadata(metadata));
  for (const patch of [
    { schemaVersion: 1 }, { resources: metadata.resources.slice(0, 2) },
    { resources: [...metadata.resources.slice(0, 4), { ...metadata.resources[4], id: "unapproved" }] },
    { actors: [actors[0], actors[0], actors[2]] }, { origin: "http://localhost:5255" },
    { origin: `${env.REFERENCE_BASE_URL}/path` }, { webClientId: "invalid" },
  ]) assert.throws(() => validateLiveMetadata({ ...metadata, ...patch }), SignInCheckError);
});

test("pending Live writes are verified using only GETs of the same operation; uncertainty is never resent", async () => {
  /** @type {string[]} */
  const urls = [];
  const original = { id: "original-operation", state: /** @type {const} */ ("pending"), messageId: null, error: null };
  let result = { ...original, state: "confirmed", messageId: "teams-message" };
  /** @type {import("@playwright/test").APIRequestContext} */
  const request = /** @type {any} */ ({
    get: async (/** @type {string} */ url) => {
      urls.push(url);
      return { status: () => 200, json: async () => result, dispose: async () => {} };
    },
  });
  assert.equal((await waitForOriginalOperation(request, env.REFERENCE_BASE_URL, "graph-pilot-chat", original, 10)).messageId, "teams-message");
  assert.deepEqual(urls, [`${env.REFERENCE_BASE_URL}/api/conversations/graph-pilot-chat/operations/original-operation`]);
  result = { ...result, id: "wrong-operation" };
  await assert.rejects(waitForOriginalOperation(request, env.REFERENCE_BASE_URL, "graph-pilot-chat", original, 10), SignInCheckError);
  await assert.rejects(waitForOriginalOperation(request, env.REFERENCE_BASE_URL, "graph-pilot-chat",
    { ...original, state: "unknown" }, 0), SignInCheckError);
});
