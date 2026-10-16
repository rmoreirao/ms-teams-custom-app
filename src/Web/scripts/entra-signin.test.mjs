import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { test } from "node:test";
import { chromium } from "@playwright/test";
import { describeEndpoint, generateTotp, readPageState, readSettings, SignInCheckError, validatePasswordEntry, verifyAuthorization, waitForSession } from "./entra-signin.mjs";

const seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
const env = {
  REFERENCE_BASE_URL: "https://dev.example.test",
  ENTRA_TENANT_ID: "11111111-1111-1111-1111-111111111111",
  ENTRA_CLIENT_ID: "22222222-2222-2222-2222-222222222222",
  ENTRA_EXPECTED_ACTOR_ID: "33333333-3333-3333-3333-333333333333",
  ENTRA_EXPECTED_USERNAME: "alex@example.test",
  TEST_USER: "alex@example.test",
  TEST_USER_PASSWORD: "unit-test-password",
  TEST_USER_TOTP_SECRET: seed,
};

test("verifies the expected session independently of a closed page and rejects invalid sessions", async () => {
  const actorId = env.ENTRA_EXPECTED_ACTOR_ID;
  let status = 200;
  let actor = actorId;
  let localAuth = false;
  const server = createServer((incoming, response) => {
    assert.equal(incoming.method, "GET");
    assert.equal(incoming.url, "/api/session");
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ id: actor, localAuth, simulated: true }));
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
    assert.deepEqual(await waitForSession(context.request, { origin, actorId }), { simulated: true });
    actor = "unexpected-actor";
    await assert.rejects(waitForSession(context.request, { origin, actorId }), SignInCheckError);
    actor = actorId;
    localAuth = true;
    await assert.rejects(waitForSession(context.request, { origin, actorId }), SignInCheckError);
    localAuth = false;
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

test("rejects password truncation and ambiguous duplicate entries without exposing values", () => {
  assert.throws(() => validatePasswordEntry("TEST_USER_PASSWORD=example#suffix"), SignInCheckError);
  assert.throws(() => validatePasswordEntry("TEST_USER_PASSWORD=one\nTEST_USER_PASSWORD=two"), SignInCheckError);
  for (const text of [
    'TEST_USER_PASSWORD="example#suffix"',
    "TEST_USER_PASSWORD='example#suffix'",
    "TEST_USER_PASSWORD=example",
    "# TEST_USER_PASSWORD=not-an-entry",
    "REFERENCE_BASE_URL=https://dev.example.test",
  ]) {
    assert.doesNotThrow(() => validatePasswordEntry(text));
  }
});

test("validates the expected fixture and normalizes a copied Base32 seed", () => {
  const settings = readSettings({ ...env, TEST_USER_TOTP_SECRET: seed.toLowerCase().replace(/.{8}/g, "$& ") });
  assert.equal(settings.seed, seed);
  assert.equal(settings.origin, env.REFERENCE_BASE_URL);
});

test("rejects absent inputs, wrong identities, unsafe targets and credential logging", () => {
  for (const key of Object.keys(env)) {
    assert.throws(() => readSettings({ ...env, [key]: "" }), SignInCheckError);
  }
  for (const override of [
    { TEST_USER: "admin@example.test" },
    { TEST_USER_TOTP_SECRET: "123456" },
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
