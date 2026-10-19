# Test data and scenario guidance

## Run correctness checks

```powershell
.\scripts\Validate.ps1 -Browser
```

Component tests use Vitest; backend tests use xUnit and `WebApplicationFactory`; browser tests exercise the running Web App with authenticated local fixture sessions. The browser runner generates a random password and salt per run, starts its own host on localhost:5367, and stops it afterward. It does not use a public test-login bypass.

Vitest requires 4.1.11 or later in the 4.x line to avoid the legacy vulnerable worker pool and mocker versions. The host explicitly references `System.Security.Cryptography.Pkcs` and the test project references `System.Security.Cryptography.Xml` with a minimum version of 10.0.13 to prevent vulnerable transitive resolutions. Preserve these security minimums when updating dependencies, regenerate the lockfiles, and retain locked restores in CI.

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

The separately approved [GraphPilot journey](graph-pilot.md#run-real-teams-validation) uses private target metadata and the explicit `--teams` switch to **create** one chat message, channel post and reply per account, then verify Graph IDs/authors/content. Without that switch the authentication-only boundary remains. Optional passkey-campaign deferral is default-off and requires both the `graph-pilot` profile and explicit `ENTRA_DEFER_OPTIONAL_REGISTRATION=true`; mandatory setup and rejected credentials still stop. The three real-Teams journeys passed on 2026-10-19 without changing MFA/security policies.

The command explicitly loads the repository-root `.env`. That file is plaintext, ignored by Git and excluded from Docker builds; keep it private and out of screenshots, logs and uploaded artifacts. Do not use `VITE_` prefixes for authentication secrets. `.env.example` lists the inputs: the approved origin, tenant/client IDs, expected fixture object ID and username, plus `TEST_ALEX_USER`, `TEST_ALEX_PASSWORD`, and `TEST_ALEX_TOTP_SECRET` for Alex. The secret is the retained Base32 setup key, not a rotating code or a QR/push-registration URL. Enter all secrets locally, never in a shared transcript. Nonsecret inputs can instead be supplied as process environment variables.

Quote password values, especially when they contain `#`: an unquoted hash begins a comment rather than remaining part of the password. Duplicate password entries or unquoted hashes are rejected before a browser is opened. Choose quoting that preserves the password's actual characters; never paste it into diagnostic output.

The opt-in authentication check defaults to Playwright's bundled Chromium. Set `ENTRA_TEST_BROWSER=msedge` only when explicitly testing installed Edge; this choice does not alter the existing local/CI suite or the user's browser profile. Install missing bundled Chromium with `npx playwright install chromium`; do not change organizational browser or authentication policies to unblock a test.

Run `npm run check:entra` and `npm run test:entra:unit` before the live check. The unit checks use public RFC 6238 vectors, locally fulfilled mock sign-in pages and a loopback-only session API; they never load `.env` or send sign-in requests to Entra. They require the same Edge/Chromium browser as the live check. The live command uses a fresh, temporary browser context, validates the Entra authority/client/callback/sign-in scopes before credential entry, selects authenticator TOTP when needed, and requires both password and TOTP submission followed by the expected Entra-backed `/api/session`. Frontend business APIs, including real-time negotiation, are blocked during this auth-only check. Unexpected consent, security setup, redirects or rejected credentials stop the check without credential retries. Persistent sign-in is declined only when its heading and a real No button are visible; a checkbox or Cancel/Back control is not sufficient. After that prompt, a bounded, read-only session check uses the shared cookie jar rather than waiting for the frontend page to remain open or render. Only an HTTP 200 session matching the expected fixture and Entra mode is success; a redirect, anonymous response or home-page load alone is not. The browser closes afterward.

The command uses the browser library directly rather than the test reporter, so it creates no authentication screenshots, traces, videos, saved browser state or error-context snapshots. Failure output is deliberately sanitized because browser-library errors can include credentials or authorization URLs. Only fixed endpoint categories, HTTP statuses, script-error counts and the failed phase are reported, never URLs, cookies or payloads. `DEBUG` and `PWDEBUG` must be unset. A successful result explicitly reports the app's `simulated` flag; authentication success is not evidence of real Teams integration.

On 2026-10-16, a fresh Alex password/TOTP sign-in with bundled Chromium verified the expected fixture identity through `/api/session`, with `localAuth=false` and `simulated=true`. Automated Edge runs disconnected after authenticated navigation; the precise Edge-specific cause remains unverified. No Teams operations or new Graph consent were part of this authentication proof.

### Additional fixture accounts

All three accounts use the same naming pattern: `TEST_ALEX_USER` / `TEST_ALEX_PASSWORD` / `TEST_ALEX_TOTP_SECRET`, with corresponding `TEST_SAM_` and `TEST_JORDAN_` prefixes. Alex's former unprefixed keys must be renamed; the runner does not fall back to another account's credentials. Store final passwords after first-use changes and each account's own retained Base32 seed; quote every password. Populate the real `.env` locally, never in chat or shared artifacts.

Complete each account's initial password change and software OATH enrollment manually in a separate InPrivate session, closing all InPrivate windows before switching accounts. Finish any remaining required security setup during one manual app sign-in. Human MFA approval is appropriate during this one-time setup, but must not be used to make an unattended verification pass. Initial credentials, when still valid, are in the corresponding DPAPI-protected credential XML beside the private tenant inventory; never print or upload them.

Run one selected account with `npm run test:entra -- alex`, `-- sam` or `-- jordan`. Omitting the argument still selects Alex. Before each check, set `ENTRA_EXPECTED_USERNAME` and `ENTRA_EXPECTED_ACTOR_ID` to that selected fixture's independently known UPN and object ID from the private inventory; tenant, client and origin stay shared. The runner validates these expectations and never falls back to another account's credentials. Request explicit approval before live sign-in checks; no batch or automatic credential retry is performed.

## Fixture manifest and personas

`scripts\test-data\manifest.json` is the public, nonsecret dataset inventory; `Fixtures.cs` generates its records. Actual Entra IDs/UPNs and credentials belong in the private bootstrap inventory.

| Persona | Expected access |
| --- | --- |
| Alex | Normal conversations and simulator test controls |
| Sam | Normal conversations; target for membership revocation and offboarding tests |
| Jordan | Restricted conversation plus limited group-chat history; the separate handover and channel conversations remain inaccessible |

The dataset includes a 45-message chat for paging, a group chat, channel posts/replies, an empty conversation, restricted access, long/multiline/Unicode text, and literal hostile markup rendered as text. It contains no existing Teams messages.

Seeding is conditional-create and repeatable: it never silently resets active tests. Startup/seed jobs verify allowlisted conversation existence, tenant/type, actor scope, and record bounds. `Invoke-Fixtures.ps1` starts the in-network job and requires successful completion rather than treating scheduling as proof that data exists.

The three-user dataset is `split-view-v2`. For an existing simulator dataset, quiesce synthetic traffic and explicitly set the `retiredFixtureActorId` ARM parameter to the previously approved retired actor ID for the seed job. The job removes only that actor from the six allowlisted fixtures, transfers its `chat-project` history boundary to Jordan, increments affected epochs/revisions, and blocks the retired identity. It preserves message bodies, other memberships and operation evidence, refuses live Graph mappings or unknown actors, and is repeatable. Clear the migration parameter after the seed succeeds, then verify the new fixture version. Do not deploy the three-user API/worker over unmigrated fixture data or use a full reset just to change personas.

Reset uses the dedicated seed job's temporary `Reset` role, explicit confirmation, and the approved simulator configuration. It replaces only the six fixture conversations, invalidates old command/cursor epochs, preserves operation evidence, and clears fixture offboarding. Capture evidence before resetting. Never reuse this tool for real Teams content.

## Main Web App scenarios

| Scenario | Expected observation |
| --- | --- |
| Sign in/reload/sign out | Valid authenticated session; no default credentials; anonymous APIs return 401; signed-out views/drafts cleared |
| Search/filter/paging | Only permitted conversations; no skipped equal-timestamp pages; known empty differs from unavailable coverage |
| New chat | Authorized loaded chats only; title search and catalog paging; no directory/creation request or send on selection; per-chat drafts and duplicate-free reuse |
| Chat search recovery/focus | Loading, empty, no-match and failure stay distinct; retry reloads the authorized catalog; Escape restores the opening control; selection focuses the composer |
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

`NewChat.test.tsx` additionally covers the explicit GraphPilot capability limits, opaque catalog continuation, draft reuse without business POSTs, authorization expiry, and the retained 30-second poll. Browser journeys exercise the actual local simulator backend, plus a controlled catalog failure, keyboard focus, light/dark themes and 1280px, 390px and 320px viewports. These checks do not send real Teams messages or establish live Graph write acceptance.

For a single known operation, `GET /api/conversations/{id}/operations/{operationId}` is actor-authorized. Do not generate another ID because the original result is unknown. Server recovery of a dispatch lease is not evidence of Graph non-commit.

## Infrastructure and deployment validation

Compile `infra\main.bicep`, `infra\subscription.bicep`, and `infra\live.bicep`; CI pins Bicep 0.48.1. The 2026-10-19 local compile used the available Azure CLI Bicep 0.46.1. Parse scripts with `scripts\Check-Scripts.ps1` and validate workflows with actionlint where available. Run actual Azure what-if only with approved environment inputs.

`tests\Scripts\EntraBootstrap.Tests.ps1` exercises subscription/tenant isolation, resumable bootstrap checkpoints, Sweden region propagation, the network prerequisite and the application identity gate without network access. Infrastructure-only evaluation may proceed with incomplete test-user provisioning, but an application deployment must reject absent fixture identities before contacting Azure.

`scripts\Test-DeployedApp.ps1` verifies the real public application shell/assets, mode-specific readiness, anonymous API denial, disabled local sign-in and the canonical Entra redirect. The Sweden browser smoke also verifies both themes with zero CSP violations and no local-login option. Public checks do not replace authenticated business journeys; the current GraphPilot evidence separately records fresh password/TOTP and actual Teams read/send/reply results for all three accounts.

`OidcCallbackTests` separately exercises the actual OIDC/MSAL authorization-code path behind a simulated internal HTTP proxy. Its controlled token endpoint captures the redemption form and checks that `redirect_uri` equals the public HTTPS authorization callback, with PKCE retained. This catches callback mismatches that redirect-only smoke cannot detect, without sending synthetic codes to real Entra. A fresh real user login remains necessary for final session acceptance.

If sign-in shows **"Need admin approval"**, resolve the separate sign-in consent gate described in the dev guide. `tests\Scripts\SignInConsent.Tests.ps1` covers the opt-in consent helper without contacting Entra: no-write planning, three user-specific standard scopes, idempotency, broader-grant refusal and assignment restriction. No CI test grants real tenant consent.

After authorized deployment, validate private DNS and data-plane managed identities from inside the VNet, direct-origin denial, Front Door/WAF, private PubSub backend/public authenticated clients, protected upstream admission, session continuity, Cosmos transaction conflicts, broker redelivery/DLQ purge, and real cache/identity failures. These cloud checks have not been substituted with mocks or declared passed by local tests.

## Real Teams gate and excluded work

Before any real Teams test, approve licensed users, resource scope, data-handling policy, Graph endpoint permissions/consent/RSC installation, request budgets, and stop conditions. The live foundation still requires an authoritative policy integration, completion of the release work and real environment validation, not merely a new client ID.

The [live runtime foundation](live-runtime.md) includes receiver/projector/delivery/control paths and remains disabled in the hosted basic GraphPilot environment. `LiveStateTests` and `LiveGraphTests` exercise normalized persistence, atomic journal/outbox, opaque-version conflicts, repair generations, authority expiry, dispatch fencing, signed publisher/tenant/audience checks, authenticated encryption, scope filtering, subscription-response recovery and missed deletions/replies. They use memory/controlled HTTP, not real Azure services.

Run `tests\Scripts\IdentityProfile.Tests.ps1` and `tests\Scripts\LiveDeployment.Tests.ps1` for isolated desired-state/rebuild and deployment preflight checks; both are included in validation and CI. No directory grant, licence assignment or Azure deployment occurs in those tests. A real policy authority and the separate G0-G7 evidence remain required; use the live guide's policy contract and staging sequence rather than inventing fixture grants.

`OutboxDeliveryTests` covers change-feed publication, leased fan-out pages, continuation enqueue loss, concurrent/expired claims, publication failure/replay and revision coalescing. `BoundedJsonTests` and Graph transport cases cover streaming payload bounds, cancellation and non-dispatch evidence; `LiveTelemetryTests` checks operational-only metric labels. `DeploymentContract.Tests.ps1` tests the simulator/GraphPilot/live public contract without HTTP calls. `GraphPilotDeployment.Tests.ps1` verifies API-only patching, no-write preview, preserved sizing/secrets, ARM normalization and stale-revision rejection. The public smoke command accepts `-ExpectedMode GraphPilot` or `Live` and an explicit production `-ExpectAdmission` stage assertion, while still reporting its own authenticated/Graph acceptance as untested.

Use normal delegated sending; do not use migration APIs, app-only impersonation, production users, or high-rate writes to create fixtures. Track real consent, Graph creation IDs, native Teams edits/deletions, callback validation/renewal/gaps, authorization changes, and cleanup separately.

No load test, stress run, soak, 200,000-user capacity claim, regional failover test, or load report is part of this delivery.
