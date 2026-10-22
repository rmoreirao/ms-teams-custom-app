import { Secret, TOTP } from "otpauth";
import { setTimeout as delay } from "node:timers/promises";

export class SignInCheckError extends Error {}

/** @param {unknown} value */
export function isLiveSession(value) {
  return !!value && typeof value === "object" &&
    "environment" in value && value.environment === "Development" &&
    "source" in value && value.source === "Microsoft Teams" &&
    "scopedValidation" in value && typeof value.scopedValidation === "boolean" &&
    "canStartDirectChats" in value && typeof value.canStartDirectChats === "boolean" &&
    "canCreateDirectChats" in value && value.canCreateDirectChats === false &&
    !["simulated", "localAuth", "scenarioAdmin", "basicValidation"].some(field => field in value);
}
/** @param {unknown} value */
export function isServiceSession(value) {
  return !!value && typeof value === "object" && isLiveSession(value) &&
    "scopedValidation" in value && value.scopedValidation === false &&
    "backgroundAuthentication" in value && value.backgroundAuthentication === "application" &&
    "entitlementProvider" in value && value.entitlementProvider === "Graph";
}

/** @param {string} [account] */
export function credentialKeys(account = "alex") {
  if (!["alex", "sam", "jordan"].includes(account)) {
    throw new SignInCheckError("Choose exactly one fixture account: alex, sam or jordan.");
  }
  const prefix = `TEST_${account.toUpperCase()}`;
  return { username: `${prefix}_USER`, password: `${prefix}_PASSWORD`, seed: `${prefix}_TOTP_SECRET` };
}

/**
 * @param {{get:(url:string, options:{maxRedirects:number,timeout:number}) => Promise<Pick<import("@playwright/test").APIResponse,"status"|"json"|"dispose">>}} request
 * @param {Pick<ReturnType<typeof readSettings>, "origin" | "actorId"> & {authProfile?:string}} settings
 * @param {number} [timeoutMs]
 */
export async function waitForSession(request, settings, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const response = await request.get(`${settings.origin}/api/session`, {
      maxRedirects: 0,
      timeout: Math.max(1, Math.min(5_000, deadline - Date.now())),
    });
    try {
      if (response.status() === 200) {
        /** @type {unknown} */
        const session = await response.json();
        if (!session || typeof session !== "object" || !("id" in session) || session.id !== settings.actorId || !isLiveSession(session)) {
          throw new SignInCheckError("The session does not match the expected Entra identity and Live contract.");
        }
        if (settings.authProfile === "service" && !isServiceSession(session))
          throw new SignInCheckError("The session did not confirm application-authenticated Graph policy; no validation-only fallback is accepted.");
        return { environment: "Development", source: "Microsoft Teams" };
      }
      if (response.status() !== 401) {
        throw new SignInCheckError(`The session endpoint returned HTTP ${response.status()}; no authenticated session was verified.`);
      }
    } finally {
      await response.dispose();
    }
    const remaining = deadline - Date.now();
    if (remaining > 0) await delay(Math.min(1_000, remaining));
  }
  throw new SignInCheckError("The application did not establish an authenticated session within the verification window.");
}

/**
 * @param {{ origin: string, used: { accountPicker: boolean, username: boolean, password: boolean,
 * alternate: boolean, otpOption: boolean, otp: boolean, staySignedIn: boolean,
 * allowRegistrationDeferral?: boolean, registrationDeferred?: boolean } }} input
 */
export function readPageState({ origin, used }) {
  /** @param {Element | null} element @returns {element is HTMLElement} */
  const isVisible = element => {
    if (!(element instanceof HTMLElement)) return false;
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 && getComputedStyle(element).visibility === "visible";
  };
  /** @param {string} selector */
  const visible = selector => isVisible(document.querySelector(selector));
  if (location.origin === origin && location.pathname === "/" && document.readyState !== "loading") return "application";
  if (location.origin !== "https://login.microsoftonline.com") return "unexpected-origin";
  const text = document.body?.innerText ?? "";
  if (/Need admin approval|Permissions requested|Review permissions/i.test(text)) return "consent";
  for (const [selector, state] of [
    ["#usernameError", "rejected-username"],
    ["#passwordError", "rejected-password"],
    ["#idSpan_SAOTCC_ErrorMsg_Identity", "rejected-mfa"],
    ["#service_exception_message", "rejected-service"],
  ]) {
    if (visible(selector) && document.querySelector(selector)?.textContent?.trim()) return state;
  }
  const registrationHeading = Array.from(document.querySelectorAll('h1, h2, [role="heading"], .text-title'))
    .filter(isVisible).some(element => /^let['\u2019]s keep your account secure$/i.test(element.innerText.trim()));
  const deferControl = Array.from(document.querySelectorAll('a, button, input[type="button"], input[type="submit"], [role="button"], [role="link"]'))
    .filter(isVisible).some(element => /^not now$/i.test(
      (element instanceof HTMLInputElement ? element.value : element.getAttribute("aria-label") ?? element.innerText).trim()));
  if (used.allowRegistrationDeferral && !used.registrationDeferred && used.password && used.otp &&
      !visible("#idTxtBx_SAOTCC_OTC") && registrationHeading && deferControl) return "optional-registration";
  if (/More information required|Keep your account secure|Update your password|Change your password|Sign in faster with your face|Create a passkey|Add a sign-in method/i.test(text)) return "setup";
  if (used.password && visible('input[name="loginfmt"]') && !visible('input[name="passwd"]')) return "unexpected-restart";
  if (!used.accountPicker && visible("#otherTile")) return "account-picker";
  if (!used.username && visible('input[name="loginfmt"]')) return "username";
  if (!used.password && visible('input[name="passwd"]')) return "password";
  if (!used.otp && visible("#idTxtBx_SAOTCC_OTC")) return "otp";
  if (!used.otpOption && (visible('[data-value="PhoneAppOTP"]') || visible('[data-value="SoftwareOath"]'))) return "otp-option";
  if (!used.alternate && (visible("#signInAnotherWay") || visible("#idA_SAOTCS_Proofs_AnotherProof") ||
      Array.from(document.querySelectorAll("a")).filter(isVisible).some(link =>
        /sign in another way|different verification option|can't use my microsoft authenticator/i.test(link.textContent ?? "")))) return "alternate";
  const persistenceHeading = Array.from(document.querySelectorAll('h1, h2, [role="heading"], .text-title'))
    .filter(isVisible).some(element => /^stay signed in\??$/i.test(element.innerText.trim()));
  const declineButton = Array.from(document.querySelectorAll('button, input[type="button"], input[type="submit"], [role="button"]'))
    .filter(isVisible).some(element => /^no$/i.test(
      (element instanceof HTMLInputElement ? element.value : element.getAttribute("aria-label") ?? element.innerText).trim()));
  if (!used.staySignedIn && persistenceHeading && declineButton) return "stay-signed-in";
  return undefined;
}

/** @param {string} value @param {string} origin */
export function describeEndpoint(value, origin) {
  if (!URL.canParse(value)) return "unavailable";
  const url = new URL(value);
  if (url.origin === origin) {
    if (url.pathname === "/") return "app-home";
    if (url.pathname === "/auth/callback") return "app-callback";
    if (url.pathname === "/api/session") return "app-session";
    return "app-other";
  }
  if (url.origin !== "https://login.microsoftonline.com") return "other-site";
  if (url.pathname.endsWith("/authorize")) return "entra-authorization";
  if (url.pathname.endsWith("/kmsi")) return "entra-persistence";
  if (url.pathname.includes("/SAS/")) return "entra-mfa";
  return "entra-other";
}

/** @param {string} text @param {string} [name] */
export function validatePasswordEntry(text, name = "TEST_ALEX_PASSWORD") {
  /** @type {string[]} */
  const entries = [];
  for (const line of text.split(/\r?\n/)) {
    const match = /^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
    if (match && match[1] === name) entries.push(match[2]);
  }
  if (entries.length > 1) throw new SignInCheckError(`Define ${name} only once in .env.`);
  const value = entries[0] ?? "";
  if (!['"', "'", "`"].some(quote => value.startsWith(quote)) && value.includes("#")) {
    throw new SignInCheckError(`Quote ${name} in .env: an unquoted # starts a comment and truncates the loaded value.`);
  }
}

/** @param {NodeJS.ProcessEnv} env @param {string} [account] */
export function readSettings(env, account = "alex") {
  const keys = credentialKeys(account);
  /** @param {string} name */
  const required = name => {
    const value = env[name];
    if (!value) throw new SignInCheckError(`Missing ${name}.`);
    return value;
  };
  if (env.DEBUG || env.PWDEBUG) {
    throw new SignInCheckError("Disable DEBUG and PWDEBUG before using authentication credentials.");
  }
  const browserChannel = env.ENTRA_TEST_BROWSER ?? "chromium";
  if (!["chromium", "msedge"].includes(browserChannel)) {
    throw new SignInCheckError("ENTRA_TEST_BROWSER must be chromium or msedge.");
  }
  const base = required("REFERENCE_BASE_URL");
  if (!URL.canParse(base)) throw new SignInCheckError("REFERENCE_BASE_URL is not a valid URL.");
  const url = new URL(base);
  if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash || url.pathname !== "/") {
    throw new SignInCheckError("REFERENCE_BASE_URL must be an HTTPS origin without credentials, path, query or fragment.");
  }
  const tenantId = required("ENTRA_TENANT_ID");
  const clientId = required("ENTRA_CLIENT_ID");
  const actorId = env[`TEST_${account.toUpperCase()}_OBJECT_ID`] ?? required("ENTRA_EXPECTED_ACTOR_ID");
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (![tenantId, clientId, actorId].every(value => guid.test(value))) {
    throw new SignInCheckError("Expected tenant, client and fixture actor IDs must be GUIDs.");
  }
  const username = required(keys.username).trim();
  const expectedUsername = env[`TEST_${account.toUpperCase()}_EXPECTED_USERNAME`] ?? required("ENTRA_EXPECTED_USERNAME");
  if (username.toLowerCase() !== expectedUsername.trim().toLowerCase()) {
    throw new SignInCheckError(`${keys.username} does not match the explicitly expected fixture account.`);
  }
  const password = required(keys.password);
  const seed = required(keys.seed).replace(/\s/g, "").toUpperCase();
  if (!/^[A-Z2-7]{16,}={0,6}$/.test(seed)) {
    throw new SignInCheckError(`${keys.seed} must contain the Base32 setup key, not a verification code or QR URL.`);
  }
  const configuredProfile = env.ENTRA_AUTH_PROFILE ?? "signin";
  const authProfile = configuredProfile === "graph-pilot" ? "live" :
    configuredProfile === "graph-pilot-direct" ? "live-delegated" : configuredProfile;
  if (!["signin", "live", "live-delegated", "service"].includes(authProfile))
    throw new SignInCheckError("ENTRA_AUTH_PROFILE must be signin, live, live-delegated or service.");
  const deferRegistration = env.ENTRA_DEFER_OPTIONAL_REGISTRATION ?? "false";
  if (!["true", "false"].includes(deferRegistration) || deferRegistration === "true" && !["live", "live-delegated"].includes(authProfile))
    throw new SignInCheckError("Optional registration deferral requires explicit Live validation opt-in.");
  return { account, origin: url.origin, tenantId, clientId, actorId, username, password, seed, browserChannel, authProfile,
    allowRegistrationDeferral: deferRegistration === "true" };
}

/**
 * @param {string | undefined} location
 * @param {ReturnType<typeof readSettings>} settings
 */
export function verifyAuthorization(location, settings) {
  if (!location || !URL.canParse(location)) throw new SignInCheckError("The app did not return an absolute authorization URL.");
  const url = new URL(location);
  const scopes = new Set((url.searchParams.get("scope") ?? "").split(/\s+/).filter(Boolean)
    .map(scope => scope.startsWith("https://graph.microsoft.com/") ? scope.slice("https://graph.microsoft.com/".length) : scope));
  const expectedScopes = ["openid", "profile", "offline_access", ...(["live", "live-delegated"].includes(settings.authProfile) ?
    ["Chat.Read", "ChatMessage.Send", "Channel.ReadBasic.All", "ChannelMessage.Read.All", "ChannelMessage.Send"] : []),
    ...(settings.authProfile === "live-delegated" ? ["Chat.Create"] : []),
    ...(settings.authProfile === "service" ? ["ChatMessage.Send", "ChannelMessage.Send"] : [])];
  if (url.origin !== "https://login.microsoftonline.com" || url.username || url.password ||
      url.pathname.toLowerCase() !== `/${settings.tenantId.toLowerCase()}/oauth2/v2.0/authorize` ||
      url.searchParams.get("client_id")?.toLowerCase() !== settings.clientId.toLowerCase() ||
      url.searchParams.get("redirect_uri") !== `${settings.origin}/auth/callback` ||
      scopes.size !== expectedScopes.length || !expectedScopes.every(scope => scopes.has(scope))) {
    throw new SignInCheckError("The authorization target differs from the approved tenant, application, callback or sign-in scopes.");
  }
  return url.href;
}

/** @param {string} seed @param {number} [timestamp] */
export function generateTotp(seed, timestamp = Date.now()) {
  return new TOTP({
    secret: Secret.fromBase32(seed),
    algorithm: "SHA1",
    digits: 6,
    period: 30,
  }).generate({ timestamp });
}
