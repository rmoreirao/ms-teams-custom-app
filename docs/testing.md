# Test data and scenario guidance

## Run correctness checks

```powershell
.\scripts\Validate.ps1 -Browser
```

Component tests use Vitest; backend tests use xUnit and `WebApplicationFactory`; browser tests exercise the running Web App with authenticated local fixture sessions. The browser runner generates a random password and salt per run, starts its own host on localhost:5367, and stops it afterward. It does not use a public test-login bypass.

CI publishes only backend results and screenshots. Browser HTML reports stay local, and tracing is off by default to avoid uploading credential-entry steps or session material. Investigative traces must remain private and be reviewed/redacted before sharing.

On Windows the browser suite uses installed Edge. On Linux/CI install the declared Playwright browser when needed:

```powershell
Push-Location src\Web
npx playwright install --with-deps chromium
npm run test:e2e
Pop-Location
```

Build the frontend and backend first when invoking the browser runner directly. CI uses Release; local defaults use Debug. Set `DOTNET_CONFIGURATION` to match an existing build when necessary.

`REFERENCE_BASE_URL` can target an existing dev origin, but the supplied local-login scenarios are not an Entra sign-in automation suite. Do not run them against Azure and call that real identity validation. For Entra validation, use the opt-in check below or a separately authenticated Playwright context after human MFA/password-change/consent, keeping its state outside source/artifacts. Never upload cookies, capability URLs, credentials, or unredacted identity traces.

## Opt-in password and TOTP sign-in check

`npm run test:entra` from `src\Web` runs a separate, read-only authentication check against an explicitly selected deployed app. It is not invoked by the local browser suite or CI. It requires a dedicated fixture user with completed first-use setup and an approved software OATH registration; it does not disable MFA, change a password, grant consent, enroll a method, or send Teams messages.

The command explicitly loads the repository-root `.env`. That file is plaintext, ignored by Git and excluded from Docker builds; keep it private and out of screenshots, logs and uploaded artifacts. Do not use `VITE_` prefixes for authentication secrets. `.env.example` lists the inputs: the approved origin, tenant/client IDs, expected fixture object ID and username, plus `TEST_USER`, `TEST_USER_PASSWORD`, and `TEST_USER_TOTP_SECRET`. The secret is the retained Base32 setup key, not a rotating code or a QR/push-registration URL. Enter all secrets locally, never in a shared transcript. Nonsecret inputs can instead be supplied as process environment variables.

Quote password values, especially when they contain `#`: an unquoted hash begins a comment rather than remaining part of the password. Duplicate password entries or unquoted hashes are rejected before a browser is opened. Choose quoting that preserves the password's actual characters; never paste it into diagnostic output.

Run `npm run check:entra` and `npm run test:entra:unit` before the live check. The unit checks use public RFC 6238 vectors, locally fulfilled mock sign-in pages and a loopback-only session API; they never load `.env` or send sign-in requests to Entra. They require the same Edge/Chromium browser as the live check. The live command uses a fresh, temporary browser context, validates the Entra authority/client/callback/sign-in scopes before credential entry, selects authenticator TOTP when needed, and requires both password and TOTP submission followed by the expected Entra-backed `/api/session`. Frontend business APIs, including real-time negotiation, are blocked during this auth-only check. Unexpected consent, security setup, redirects or rejected credentials stop the check without credential retries. Persistent sign-in is declined only when its heading and a real No button are visible; a checkbox or Cancel/Back control is not sufficient. After that prompt, a bounded, read-only session check uses the shared cookie jar rather than waiting for the frontend page to remain open or render. Only an HTTP 200 session matching the expected fixture and Entra mode is success; a redirect, anonymous response or home-page load alone is not. The browser closes afterward.

The command uses the browser library directly rather than the test reporter, so it creates no authentication screenshots, traces, videos, saved browser state or error-context snapshots. Failure output is deliberately sanitized because browser-library errors can include credentials or authorization URLs. Only fixed endpoint categories, HTTP statuses, script-error counts and the failed phase are reported, never URLs, cookies or payloads. `DEBUG` and `PWDEBUG` must be unset. A successful result explicitly reports the app's `simulated` flag; authentication success is not evidence of real Teams integration.

## Fixture manifest and personas

`scripts\test-data\manifest.json` is the public, nonsecret dataset inventory; `Fixtures.cs` generates its records. Actual Entra IDs/UPNs and credentials belong in the private bootstrap inventory.

| Persona | Expected access |
| --- | --- |
| Alex | Normal conversations and simulator test controls |
| Sam | Normal conversations; target for membership revocation and offboarding tests |
| Jordan | Only the restricted conversation; normal conversation IDs must return 404 |
| Taylor | Group chat with an explicit history cutoff; old content and hidden thread parents remain inaccessible |

The dataset includes a 45-message chat for paging, a group chat, channel posts/replies, an empty conversation, restricted access, long/multiline/Unicode text, and literal hostile markup rendered as text. It contains no existing Teams messages.

Seeding is conditional-create and repeatable: it never silently resets active tests. Startup/seed jobs verify allowlisted conversation existence, tenant/type, actor scope, and record bounds. `Invoke-Fixtures.ps1` starts the in-network job and requires successful completion rather than treating scheduling as proof that data exists.

Reset uses the dedicated seed job's temporary `Reset` role, explicit confirmation, and the approved simulator configuration. It replaces only the six fixture conversations, invalidates old command/cursor epochs, preserves operation evidence, and clears fixture offboarding. Capture evidence before resetting. Never reuse this tool for real Teams content.

## Main Web App scenarios

| Scenario | Expected observation |
| --- | --- |
| Sign in/reload/sign out | Valid authenticated session; no default credentials; anonymous APIs return 401; signed-out views/drafts cleared |
| Search/filter/paging | Only permitted conversations; no skipped equal-timestamp pages; known empty differs from unavailable coverage |
| Chat send / channel post / reply | One operation ID per submission; pending before explicit simulator confirmation; thread retains parent context |
| Rejected send | No created message; deliberate editing/new attempt only |
| Unknown send | No automatic resend; original-operation check resolves exact simulator receipt; restart does not invent a new send |
| External message/edit/delete | Authenticated control enqueues an event; worker applies it and hints a refetch; deleted content is not resurrected |
| Membership removal | Existing view/read access denied; current recipient eligibility checked before hints |
| Offboarding | Durable application deny applies despite old memberships/session; client connection closed and volatile view cleared |
| Offline/reconnect | Sending disabled offline, no offline queue, in-page drafts preserved, authorized content refetched on reconnect |
| History/policy boundary | Restricted fixture content and hidden parents never appear; real Graph/DLP equivalence is not claimed |
| Browser safety | User text is not HTML; desktop/mobile navigation, keyboard/focus, themes, wrapping, and zero business browser storage |

Alex's **Test scenarios** menu injects next-send rejection, delay, throttling, and lost confirmation, plus external messages/edits/deletes and Sam's membership removal/restoration. Global offboarding is an administrator-only fixture API exercised by the browser suite. None of these controls is an anonymous or real-tenant administrative endpoint.

For a single known operation, `GET /api/conversations/{id}/operations/{operationId}` is actor-authorized. Do not generate another ID because the original result is unknown. Server recovery of a dispatch lease is not evidence of Graph non-commit.

## Infrastructure and deployment validation

Compile both `infra\main.bicep` and `infra\subscription.bicep` with Bicep 0.48.1+. Parse scripts with `scripts\Check-Scripts.ps1` and validate workflows with actionlint. Run actual Azure what-if only with approved environment inputs.

`tests\Scripts\EntraBootstrap.Tests.ps1` exercises subscription/tenant isolation, resumable bootstrap checkpoints, Sweden region propagation, the network prerequisite and the application identity gate without network access. Infrastructure-only evaluation may proceed with incomplete test-user provisioning, but an application deployment must reject absent fixture identities before contacting Azure.

`scripts\Test-DeployedApp.ps1` verifies the real public application shell/assets, fixture/Redis readiness, anonymous API denial, disabled local sign-in and the canonical Entra redirect. The current Sweden browser smoke also verifies both themes with zero CSP violations and no local-login option. These public checks do not replace authenticated business journeys: the first real test login currently requires completion of Entra security enrollment by the user.

`OidcCallbackTests` separately exercises the actual OIDC/MSAL authorization-code path behind a simulated internal HTTP proxy. Its controlled token endpoint captures the redemption form and checks that `redirect_uri` equals the public HTTPS authorization callback, with PKCE retained. This catches callback mismatches that redirect-only smoke cannot detect, without sending synthetic codes to real Entra. A fresh real user login remains necessary for final session acceptance.

If sign-in shows **"Need admin approval"**, resolve the separate sign-in consent gate described in the dev guide. `tests\Scripts\SignInConsent.Tests.ps1` covers the opt-in consent helper without contacting Entra: no-write planning, four user-specific standard scopes, idempotency, broader-grant refusal and assignment restriction. No CI test grants real tenant consent.

After authorized deployment, validate private DNS and data-plane managed identities from inside the VNet, direct-origin denial, Front Door/WAF, private PubSub backend/public authenticated clients, protected upstream admission, session continuity, Cosmos transaction conflicts, broker redelivery/DLQ purge, and real cache/identity failures. These cloud checks have not been substituted with mocks or declared passed by local tests.

## Real Teams gate and excluded work

Before any real Teams test, approve licensed users, resource scope, data-handling policy, Graph endpoint permissions/consent/RSC installation, request budgets, and stop conditions. The disabled live runtime needs further callback/control/entitlement implementation and integration validation, not merely a new client ID.

Use normal delegated sending; do not use migration APIs, app-only impersonation, production users, or high-rate writes to create fixtures. Track real consent, Graph creation IDs, native Teams edits/deletions, callback validation/renewal/gaps, authorization changes, and cleanup separately.

No load test, stress run, soak, 200,000-user capacity claim, regional failover test, or load report is part of this delivery.
