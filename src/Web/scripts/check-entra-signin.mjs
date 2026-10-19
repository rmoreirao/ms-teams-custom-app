import { chromium } from "@playwright/test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { loadEnvFile } from "node:process";
import { setTimeout as delay } from "node:timers/promises";
import { credentialKeys, describeEndpoint, generateTotp, readPageState, readSettings, SignInCheckError, validatePasswordEntry, verifyAuthorization, waitForSession } from "./entra-signin.mjs";
import { applyPilotSettings, validateTeams } from "./teams-validation.mjs";

let stage = "load local configuration";

async function main() {
  if (process.argv.length > 4 || process.argv[3] && process.argv[3] !== "--teams")
    throw new SignInCheckError("Specify one fixture account and optional --teams validation.");
  const account = process.argv[2] ?? "alex";
  const teams = process.argv[3] === "--teams";
  const keys = credentialKeys(account);
  const envPath = resolve(import.meta.dirname, "..", "..", "..", ".env");
  validatePasswordEntry(readFileSync(envPath, "utf8"), keys.password);
  loadEnvFile(envPath);
  const settingsPath = process.env.PILOT_VALIDATION_SETTINGS;
  const pilotMetadata = settingsPath ? JSON.parse(readFileSync(settingsPath, "utf8")) : null;
  if (pilotMetadata) applyPilotSettings(pilotMetadata, account);
  if (teams && !settingsPath) throw new SignInCheckError("PILOT_VALIDATION_SETTINGS must name the private approved resource inventory.");
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
    const configResponse = await context.request.get(`${settings.origin}/auth/config`, { maxRedirects: 0 });
    /** @type {unknown} */
    const config = await configResponse.json();
    if (configResponse.status() !== 200 || !config || typeof config !== "object" ||
        !("localAuth" in config) || config.localAuth !== false) {
      throw new SignInCheckError("The target app does not report Entra-only authentication.");
    }
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
    const teamsProof = teams && settingsPath && pilotMetadata ?
      await validateTeams(context, page, settings, pilotMetadata, settingsPath, value => { stage = value; }) : null;
    console.log(JSON.stringify({
      account: settings.account,
      browser: settings.browserChannel,
      authenticated: true,
      expectedFixtureMatched: true,
      freshTotpChallengeCompleted: true,
      optionalRegistrationDeferred: used.registrationDeferred,
      localAuth: false,
      simulated: session.simulated,
      teamsOperationsPerformed: teams,
      ...(teamsProof ? { teamsProof } : {}),
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

// Playwright errors can contain credential values and authorization URLs in call logs.
main().catch(error => {
  const kind = error instanceof Error && ["TimeoutError", "TargetClosedError", "TypeError"].includes(error.name) ? error.name : "Error";
  console.error(error instanceof SignInCheckError
    ? `Entra sign-in check failed: ${error.message}`
    : `Entra sign-in check failed during "${stage}" (${kind}). Diagnostic details withheld to protect authentication material.`);
  process.exitCode = 1;
});
