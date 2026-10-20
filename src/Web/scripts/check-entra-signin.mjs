import { chromium } from "@playwright/test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { loadEnvFile } from "node:process";
import { setTimeout as delay } from "node:timers/promises";
import { credentialKeys, describeEndpoint, generateTotp, readPageState, readSettings, SignInCheckError, validatePasswordEntry, verifyAuthorization, waitForSession } from "./entra-signin.mjs";
import { applyLiveSettings, validateDirectChat, validateTeams } from "./teams-validation.mjs";

let stage = "load local configuration";

/**
 * @param {string} account
 * @param {null | ((value: {
 *   context: import("@playwright/test").BrowserContext,
 *   page: import("@playwright/test").Page,
 *   settings: ReturnType<typeof readSettings>,
 *   session: Awaited<ReturnType<typeof waitForSession>>,
 *   stage: (value: string) => void
 * }) => Promise<unknown>)} onAuthenticated
 * @param {"teams" | "direct-chat" | null} validation
 */
export async function withFixtureSession(account = "alex", onAuthenticated = null, validation = null) {
  const teams = validation === "teams";
  const direct = validation === "direct-chat";
  if (direct && account !== "jordan") throw new SignInCheckError("Direct-chat validation is restricted to the approved Jordan-to-Alex journey.");
  const keys = credentialKeys(account);
  const envPath = resolve(import.meta.dirname, "..", "..", "..", ".env");
  validatePasswordEntry(readFileSync(envPath, "utf8"), keys.password);
  loadEnvFile(envPath);
  const settingsPath = process.env.LIVE_VALIDATION_SETTINGS;
  const metadata = settingsPath ? JSON.parse(readFileSync(settingsPath, "utf8")) : null;
  if (metadata) applyLiveSettings(metadata, account);
  if ((teams || direct) && !settingsPath) throw new SignInCheckError("LIVE_VALIDATION_SETTINGS must name the private approved Live resource inventory.");
  if (process.env.PILOT_VALIDATION_SETTINGS)
    throw new SignInCheckError("Migrate PILOT_VALIDATION_SETTINGS to LIVE_VALIDATION_SETTINGS with schemaVersion 2; existing resource IDs and consent must remain unchanged.");
  const settings = readSettings(process.env, account);
  stage = "launch an isolated browser";
  const browser = await chromium.launch({
    headless: true,
    channel: settings.browserChannel,
  });
  /** @type {import("@playwright/test").Page | undefined} */
  let currentPage;
  /** @type {{ endpoint: string, status: number }[]} */
  const responses = [];
  let scriptErrors = 0;
  try {
    const context = await browser.newContext({ locale: "en-US" });
    // Prevent the frontend from starting business operations during this auth-only check.
    await context.route(`${settings.origin}/api/**`, route =>
      route.request().method() === "GET" && new URL(route.request().url()).pathname === "/api/session"
        ? route.continue()
        : route.abort("blockedbyclient"));
    const page = await context.newPage();
    currentPage = page;
    page.on("pageerror", () => { scriptErrors++; });
    page.on("response", response => {
      const request = response.request();
      if (request.isNavigationRequest() ||
          (new URL(response.url()).origin === "https://login.microsoftonline.com" &&
           ["xhr", "fetch"].includes(request.resourceType()))) {
        responses.push({ endpoint: describeEndpoint(response.url(), settings.origin), status: response.status() });
        if (responses.length > 20) responses.shift();
      }
    });
    page.setDefaultTimeout(30_000);
    page.setDefaultNavigationTimeout(45_000);
    stage = "verify the application authentication boundary";
    const anonymous = await context.request.get(`${settings.origin}/api/session`, { maxRedirects: 0 });
    if (anonymous.status() !== 401) throw new SignInCheckError("The target must reject anonymous sessions.");
    const challenge = await context.request.get(`${settings.origin}/auth/signin`, { maxRedirects: 0 });
    if (challenge.status() !== 302) throw new SignInCheckError("The app did not return an authorization redirect.");
    const authorization = verifyAuthorization(challenge.headers().location, settings);
    stage = "open the approved Entra authorization page";
    await page.goto(authorization, { waitUntil: "domcontentloaded" });
    const used = { accountPicker: false, username: false, password: false, alternate: false, otpOption: false, otp: false,
      staySignedIn: false, allowRegistrationDeferral: settings.allowRegistrationDeferral, registrationDeferred: false };
    let previousState = "authorization";
    authentication: for (let step = 0; step < 10; step++) {
      stage = `wait for the next state after ${previousState}`;
      const stateHandle = await page.waitForFunction(readPageState, { origin: settings.origin, used }, { timeout: 45_000 });
      const state = await stateHandle.jsonValue();
      await stateHandle.dispose();
      previousState = state ?? "unknown";
      stage = `handle ${state}`;
      console.log(`Entra sign-in state: ${state}`);
      if (state === "application") break;
      if (state === "unexpected-origin") throw new SignInCheckError("Stopped before entering credentials on an unexpected origin.");
      if (state === "unexpected-restart") throw new SignInCheckError("Entra restarted the sign-in flow; no automatic credential retry was attempted.");
      if (state === "consent") throw new SignInCheckError("Entra requested consent; no permissions were granted.");
      if (state === "setup") {
        await delay(1500);
        const candidate = await context.request.get(`${settings.origin}/api/session`, { maxRedirects: 0 });
        if (candidate.status() === 200) break authentication;
        if (!page.isClosed() && await page.evaluate(readPageState, { origin: settings.origin, used }) !== "setup") continue;
        throw new SignInCheckError("Entra requires user-performed password or security-info setup.");
      }
      if (state?.startsWith("rejected-")) {
        const providerCode = await page.evaluate(() => /\bAADSTS\d+\b/.exec(document.body?.innerText ?? "")?.[0]);
        throw new SignInCheckError(`Entra reported ${state}${providerCode ? ` (${providerCode})` : ""}; no credential retry was attempted.`);
      }
      if (new URL(page.url()).origin !== "https://login.microsoftonline.com") {
        throw new SignInCheckError("The credential-entry origin changed unexpectedly.");
      }
      switch (state) {
        case "account-picker":
          used.accountPicker = true;
          await page.locator("#otherTile").click();
          break;
        case "username":
          used.username = true;
          await page.locator('input[name="loginfmt"]').fill(settings.username);
          await page.locator("#idSIButton9").click();
          break;
        case "password":
          used.password = true;
          await page.locator('input[name="passwd"]').fill(settings.password);
          await page.locator("#idSIButton9").click();
          break;
        case "alternate":
          used.alternate = true;
          await page.locator("#signInAnotherWay:visible, #idA_SAOTCS_Proofs_AnotherProof:visible")
            .or(page.getByRole("link", { name: /sign in another way|different verification option|can't use my microsoft authenticator/i })).first().click();
          break;
        case "otp-option":
          used.otpOption = true;
          await page.locator('[data-value="PhoneAppOTP"]:visible, [data-value="SoftwareOath"]:visible').first().click();
          break;
        case "otp": {
          if (!used.otpOption && !/authenticator app|authentication app/i.test(
            await page.locator("#idDiv_SAOTCC_Description").innerText())) {
            throw new SignInCheckError("The code prompt was not identified as authenticator TOTP; no code was submitted.");
          }
          used.otp = true;
          const remaining = 30_000 - Date.now() % 30_000;
          if (remaining < 5_000) await delay(remaining + 250);
          if (new URL(page.url()).origin !== "https://login.microsoftonline.com") {
            throw new SignInCheckError("The TOTP-entry origin changed unexpectedly.");
          }
          await page.locator("#idTxtBx_SAOTCC_OTC").fill(generateTotp(settings.seed));
          await page.locator("#idSubmit_SAOTCC_Continue").click();
          break;
        }
        case "stay-signed-in":
          used.staySignedIn = true;
          await page.getByRole("button", { name: "No", exact: true }).click();
          break authentication;
        case "optional-registration":
          used.registrationDeferred = true;
          await page.getByRole("link", { name: "Not now", exact: true })
            .or(page.getByRole("button", { name: "Not now", exact: true })).first().click();
          break;
        default:
          throw new SignInCheckError("An unsupported authentication state was reached.");
      }
    }
    stage = "verify the fresh authenticated session";
    if (!used.password || !used.otp) {
      throw new SignInCheckError("A fresh password-plus-TOTP sign-in was not demonstrated.");
    }
    const session = await waitForSession(context.request, settings);
    const teamsProof = teams && settingsPath && metadata ?
      await validateTeams(context, page, settings, metadata, settingsPath, value => { stage = value; }) : null;
    const directProof = direct && settingsPath && metadata ?
      await validateDirectChat(context, page, settings, metadata, settingsPath, value => { stage = value; }) : null;
    const additionalProof = onAuthenticated ?
      await onAuthenticated({ context, page, settings, session, stage: value => { stage = value; } }) : null;
    console.log(JSON.stringify({
      account: settings.account,
      browser: settings.browserChannel,
      authenticated: true,
      expectedFixtureMatched: true,
      freshTotpChallengeCompleted: true,
      optionalRegistrationDeferred: used.registrationDeferred,
      environment: session.environment,
      source: session.source,
      teamsOperationsPerformed: teams || direct || typeof additionalProof === "object" && additionalProof !== null &&
        "teamsOperationsPerformed" in additionalProof && additionalProof.teamsOperationsPerformed === true,
      ...(teamsProof ? { teamsProof } : {}),
      ...(directProof ? { directProof } : {}),
      ...(additionalProof ? { additionalProof } : {}),
    }, null, 2));
  } catch (error) {
    console.error(JSON.stringify({
      account: settings.account,
      browser: settings.browserChannel,
      phase: stage,
      endpoint: describeEndpoint(currentPage?.url() ?? "", settings.origin),
      pageClosed: currentPage?.isClosed() ?? true,
      browserConnected: browser.isConnected(),
      scriptErrors,
      responses,
    }, null, 2));
    throw error;
  } finally {
    await browser.close();
  }
}

async function main() {
  if (process.argv.length > 4 || process.argv[3] && !["--teams", "--direct-chat"].includes(process.argv[3]))
    throw new SignInCheckError("Specify one fixture account and optional --teams or --direct-chat validation.");
  await withFixtureSession(process.argv[2] ?? "alex", null,
    process.argv[3] === "--teams" ? "teams" : process.argv[3] === "--direct-chat" ? "direct-chat" : null);
}

// Playwright errors can contain credential values and authorization URLs in call logs.
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main().catch(error => {
  const kind = error instanceof Error && ["TimeoutError", "TargetClosedError", "TypeError"].includes(error.name) ? error.name : "Error";
  console.error(error instanceof SignInCheckError
    ? `Entra sign-in check failed: ${error.message}`
    : `Entra sign-in check failed during "${stage}" (${kind}). Diagnostic details withheld to protect authentication material.`);
  process.exitCode = 1;
});
