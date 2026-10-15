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

`REFERENCE_BASE_URL` can target an existing dev origin, but the supplied local-login scenarios are not an Entra sign-in automation suite. Do not run them against Azure and call that real identity validation. For Entra validation, use a separately authenticated Playwright context after human MFA/password-change/consent and keep its state outside source/artifacts. Never upload cookies, capability URLs, credentials, or unredacted identity traces.

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

After authorized deployment, validate private DNS and data-plane managed identities from inside the VNet, direct-origin denial, Front Door/WAF, private PubSub backend/public authenticated clients, protected upstream admission, session continuity, Cosmos transaction conflicts, broker redelivery/DLQ purge, and real cache/identity failures. These cloud checks have not been substituted with mocks or declared passed by local tests.

## Real Teams gate and excluded work

Before any real Teams test, approve licensed users, resource scope, data-handling policy, Graph endpoint permissions/consent/RSC installation, request budgets, and stop conditions. The disabled live runtime needs further callback/control/entitlement implementation and integration validation, not merely a new client ID.

Use normal delegated sending; do not use migration APIs, app-only impersonation, production users, or high-rate writes to create fixtures. Track real consent, Graph creation IDs, native Teams edits/deletions, callback validation/renewal/gaps, authorization changes, and cleanup separately.

No load test, stress run, soak, 200,000-user capacity claim, regional failover test, or load report is part of this delivery.
