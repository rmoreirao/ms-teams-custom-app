# Testing the Live-only development application

## Offline correctness checks

```powershell
.\scripts\Validate.ps1 -Browser
```

Restore locked dependencies when setting up the checkout, then use the validation script. It builds the frontend, runs component and backend tests, parses management scripts, exercises mocked deployment/identity contracts, and optionally runs browser checks. CI also compiles the Bicep entry points.

There is no selectable simulator, GraphPilot runtime, local persona/password login, or deployed fixture-control endpoint. Offline dependencies exist only in test assemblies/runners:

| Layer | What runs | What it does not prove |
| --- | --- | --- |
| xUnit API tests | Actual Live routes, middleware, authorization and CSRF through TestServer; injected test authentication/Graph/storage | Real Entra, Azure networking or Graph acceptance |
| Live backend tests | Actual state/projection/authority/control/crypto/delivery code with memory partition documents or controlled HTTP | Service capacity, real subscriptions or source propagation bounds |
| Vitest | Actual components/hooks with focused API/PubSub dependency mocks | Backend authorization or actual Web PubSub transport |
| Playwright | Compiled application shell with runner-installed API/WebSocket fixtures | A deployed host, Entra session, or real Teams message |
| Script tests | CLI/HTTP mocks, no-write previews, target/configuration/rollout guards | Actual permissions, quotas, cloud rollout or identity drift |

The test host composes the same Live application with test-owned DI replacements; no configuration flag can enable those replacements in normal startup. Test stores, command budgets and local event delivery are not published in the application assemblies. Browser test fixtures are not a normal application launcher or production mode.

Keep Vitest at least 4.1.11 in the 4.x line and the pinned cryptography dependencies at least 10.0.13; preserve locked restores. Reports must not include credentials, cookies, capability URLs or business content. Browser traces remain off by default.

For browser prerequisites:

```powershell
Push-Location src\Web
npx playwright install --with-deps chromium
npm run build
npm run test:e2e
Pop-Location
```

Windows tests use installed Edge; Linux/CI uses the configured Playwright Chromium. The offline runner owns its loopback static server and does not reuse an unrelated process. Do not redirect mocked browser scenarios to Azure and label that real integration evidence.

## Behaviors that must remain covered

| Scenario | Required observation |
| --- | --- |
| Authentication and API contract | Entra-only UI; anonymous API denial; no local login/scenario routes or legacy capability fields |
| Scope/history/offboarding | Only current source-authorized conversations/messages; hidden parents and revoked identities remain inaccessible |
| Sending | Optimistic nonblocking composer; independently confirmed status icons; paced starts without waiting for earlier responses; one operation ID/dispatch, explicit confirmed/rejected/unknown outcomes, actor-bound status and CSRF |
| Unknown or expired claims | No automatic resend, no fake receipt resolution, no inferred non-commit from a lost response |
| Throttling | Shared budgets/cooldown; rejection before dispatch when no capacity |
| Paged history | Stable equal-timestamp ordering, no skipped/duplicate pages, preserved loaded history on deltas |
| Source events | Signed/encrypted intake, durable notification, projection/outbox, authorized body-free delivery and catch-up |
| Missing coverage | Existing authorized history remains usable; known-empty succeeds; genuinely cold views require durable refresh and typed pending rather than fabricated emptiness |
| Cold/partial hydration | Initiating-request recovery, shared replica lease, cancellation independence, bounded pages/continuation, parent eligibility, conditional progress and existing merge/outbox |
| Session replica lag | Explicit lag rather than false journal reset; monotonic revisions, access-bounded page retention, immediate history filtering, local expiry and a non-resetting retry budget |
| Direct chats | Existing approved exact-pair mapping only; stale/changed membership denied; no creation/send on selection |
| Service-authenticated policy | No retained-user context in normal Control; explicit app-only resource/policy scopes; current direct app assignment, enabled/member account state, history and exact pairs; unsupported/missing source evidence denied |
| Isolated service release | Five distinct workloads/identities; API cannot attach Control; reviewed read-only authority access; draft/extra compact privileges refused; no-write plan/WhatIf, exact full rollout, partial-failure preservation and idempotent reapply |
| Personal-chat test messages | Header action beside Change person in verified one-to-one chats only; popup defaults to 30 and editable text "Test message"; exact numbered bodies, distinct operations, paced overlapping requests, preserved drafts, explicit outcome counts, continuation without retries, and cancellation of remaining sends |
| Realtime/reconnect | Web PubSub and targeted changes; no 30-second catalog polling or SSE fallback |
| UX and safety | Desktop/mobile focus/navigation, drafts in memory, offline send disabled, no business browser storage, sanitized rich content, virtualization |
| Release | All selected targets preflighted before mutation; five-role queue properties/exact scoped grants checked read-only; image-only preservation; exact readiness; explicit partial-rollout errors |

`MessagingTests` now exercises actual Live persistence and controlled Graph outcomes instead of a stateful simulator. `LiveDiscoveryTests` covers authorized existing-pair discovery and no creation. `LiveJourneyTests` traverses intake, authenticated decryption, projection, durable outbox, leased delivery and catch-up with controlled dependencies.

`LiveControlTests`, `LiveStateTests`, `LiveGraphTests`, `OutboxDeliveryTests`, `PrimaryReadTests`, `BoundedJsonTests`, `PubSubAdmissionTests`, `OidcCallbackTests`, and `LiveTelemetryTests` retain their specific correctness boundaries. The opt-in real Cosmos probe stays skipped unless explicitly configured; offline tests never silently use real services.

`SessionReadTests` uses independent current/content observations to force stale
or missing message documents. It checks document revision versus journal revision,
explicit lag evidence, current parent validation, revocation precedence without a coverage display gate,
missing-procedure refusal and unchanged operation/epoch fencing. `ApiTests` checks
the actual serialized lag/access contract. `LiveSync.test.tsx` covers revision
40 -> target 42 -> response 41 -> convergence 42, tightened history during lag,
access/retention expiry, epoch changes, obsolete responses, coalesced in-flight
hints and exhausted retry budgets. The compiled-browser replica-lag journey
also verifies that loaded history remains rendered and drafts are not sent.

`ReadRefreshTests` distinguishes available, cold and known-empty views, including
real legacy initialization without fabricated timestamps. It covers optional
versus required queue failures, same-request hydration, replica coalescing,
original work/lease takeover, HTTP cancellation, retry evidence, scope/epoch/URL
validation, partial older-page progress, channel ordering, three-page bounds,
unknown/deleted/newly appeared parents, history tightening during a cache read,
conditional marker writes and newer-deletion protection. Progress must not invent
global healthy coverage or another material revision.

`ApiTests` checks actual `202` pending serialization separately from send `202`.
`LiveSync.test.tsx` covers malformed/primitive read payloads, cold and older-page
automatic recovery, retained operational failures, proactive authority renewal
without hot polling, explicit critical-guard failures and expiry. The compiled
browser covers first-open loading and source-outage history/draft retention
without a blocking synchronization alert. These controlled tests prove neither
real Service Bus access nor Graph recovery timing.

### Session storage checks

The runtime requires explicit `readConsistency: "Session"`. Tests must reject
missing/Strong/SessionPrimary runtime profiles while accepting a compatible
single-region Session or Strong account. There is no legacy runtime alias.

After separate approval for synthetic writes and cleanup, from the approved
private network with existing Entra data access and `readCurrentV1` provisioned:

```powershell
$env:COSMOS_SESSION_TEST = '1'
$env:COSMOS_SESSION_ENDPOINT = 'https://<approved-account>.documents.azure.com/'
$env:COSMOS_SESSION_CONTAINER = 'session-primary-validation-<approved-isolated-suffix>'
dotnet test .\tests\Teams.Tests\Teams.Tests.csproj --filter FullyQualifiedName~CosmosSessionReadTests
```

`CosmosSessionReadTests` reuses the same `SessionReadProbe` as the `SessionCheck`
diagnostic, rather than maintaining a duplicate check. They exercise independent
Gateway/Session clients, bounded content convergence and current revocation/epoch
fencing. They never send Graph messages or manually distribute writer tokens.
The existing `session-primary-validation-*` container name is a preserved resource
identity, not a selectable runtime profile; do not create/rename resources merely
to remove its historical spelling. Tracked synthetic documents are cleaned up;
the test does not delete the container or alter grants/account settings.

Ordinary CI skips this check explicitly. Deterministic offline lag tests remain
necessary because a real service need not exhibit lag during a particular run.
Before/after RU, latency and stored-procedure counts require separate deployment
evidence. Follow the [configuration migration runbook](live-runtime.md#session-configuration-migration);
routine image-only releases must refuse old configuration instead of changing it.

`LiveOnly.Tests.ps1` and assembly/host tests prevent legacy runtime/configuration, fixture roles, local credentials and test doubles from reentering published code. `DevDeployment.Tests.ps1` and `DeploymentContract.Tests.ps1` cover the Live-only release/smoke contracts. `LiveSetup.Tests.ps1`, `EntraBootstrap.Tests.ps1`, `SignInConsent.Tests.ps1`, `IdentityProfile.Tests.ps1` and `LiveDeployment.Tests.ps1` cover setup and identity safety without remote operations.

Standalone `prototypes\` and `test:prototypes` remain fictional design references; their simulated interactions are not executable backend support.

## Real-service authentication and Teams checks

These commands are **not** ordinary CI and require explicit authorization for their exact external actions. They do not grant consent, change MFA/passwords, add licences or bypass mandatory setup.

Use private `.env`/process settings. Never use `VITE_` for secrets. Keep each account's own quoted password, Base32 TOTP seed, expected object ID and expected UPN; no account fallback or automatic credential retry is permitted. `DEBUG`/`PWDEBUG` must be unset.

The private `LIVE_VALIDATION_SETTINGS` metadata is schema version 2: pinned HTTPS origin, tenant/client GUIDs, exactly three distinct Alex/Sam/Jordan persona identities and all five existing approved resources. Preserve `graph-pilot-*` resource IDs and deterministic pair IDs because they identify persisted real resources, not an active runtime. Replace the old `PILOT_VALIDATION_SETTINGS` variable; it is intentionally rejected. `New-SyntheticLiveSettings.ps1` exports current Live metadata privately.

Authentication profiles are `live` and `live-delegated`. Both require `openid profile offline_access` plus the existing five delegated read/send scopes. `live-delegated` additionally verifies the already-consented `Chat.Create` scope; it does not enable creation. Historical profile-name aliases normalize without changing scopes, but new private configuration should use the Live names. Do not revoke/regrant consent or broaden users just for renaming.

Run helper type/unit checks before external use:

```powershell
Push-Location src\Web
npm run check:entra
npm run test:entra:unit
# After approval of the selected real account's sign-in:
npm run test:entra -- alex
Pop-Location
```

The authentication-only command pins authority, client, callback, scopes and expected session/source before reporting success. It uses a fresh temporary browser, validates password/TOTP transitions, rejects unexpected consent/security setup and does not send Teams messages. Complete first-use/password/MFA setup manually. Optional registration deferral remains explicitly opted in, not a way around mandatory setup. Keep all state and investigation evidence private.

These separately approved switches **send actual Teams messages**:

```powershell
# From src\Web; only after approval of these exact scoped sends:
npm run test:entra -- alex --teams
npm run test:entra -- jordan --direct-chat
node .\scripts\validate-azure-event-path.mjs --verify-azure
```

After the separately approved service-identity cutover, the private metadata can select `authProfile: "service"` (or set `ENTRA_AUTH_PROFILE=service`). This pins only the delegated user-send scopes and requires `/api/session` to report `backgroundAuthentication=application`, `entitlementProvider=Graph`, and `scopedValidation=false`. It does not grant permissions or start background authentication from a user session. Existing legacy helper profiles remain for the pre-cutover deployment only. These helpers still require separate approval for the selected real sign-in and any actual sends.

`--teams` sends one chat message, one channel post and one reply for the selected account. `--direct-chat` reuses the approved pair and sends exactly one message; it does not create a chat. The Azure event-path runner uses `AZURE_LIVE_SETTINGS`, approved live authentication metadata and Probe/VerifyProbe to distinguish external Graph creation from actual notification/projection/delivery evidence. Never replay an unresolved source probe.

`EnsurePairs` now verifies the three configured existing exact-pair mappings and preserves each approved history cutoff. It no longer uses the retired pilot store or creates Teams chats.

## Infrastructure and release evidence

Compile the CI-listed Bicep entry points, including `infra\main.bicep`, `subscription.bicep`, `live.bicep`, `synthetic-live.bicep`, `service-dev.bicep` and `service-dev-access.bicep`, with the pinned toolchain. The parent templates compile the read-refresh/monitoring modules. Compilation is not real what-if or cloud acceptance. Actual provisioning, directory changes, workflow dispatch, image push and user admission are separate approved operations.

After an authorized release, read-only smoke must establish the exact new API/worker ready revisions, Live mode, expected admission, shell/assets, anonymous denial and canonical Entra redirect. A passing local suite does not establish real Graph integration for a new image.

For the five-role service deployment, `Inspect` runs only under Control and checks actual Graph-owned rich subscription bindings, repair state and current source authority. `ProjectionCheck` runs only under Api and checks the real private data plane with Api's own managed identity: each configured user's exact catalog, bounded history-filtered snapshots, exclusion from the other direct pair, and verified existing-contact discovery/opening. It explicitly rejects unhealthy coverage with `projection_unhealthy`; availability-first reads alone cannot certify source health. Neither operation sends messages or logs message bodies/tokens. Run in the existing manual job with automatic retries disabled and restore its original definition afterward. These are service/data-plane evidence, not a replacement for a normally authenticated browser journey.

The 2026-10-21 Azure `ProjectionCheck` proof recorded three actors, twelve authorized snapshot reads, three excluded-pair denials and six verified direct-chat openings. The corresponding offline diagnostic tests reject missing coverage, expired policy and excess pair admission. Live acceptance still separately requires a normally authenticated journey and elapsed unattended renewal; local time advancement is not proof of an eight-hour Azure observation.

The first unattended sample exposed a real coverage gap while a large chat's bounded verification was still progressing; that failed evidence was retained, not declared a pass. Renewal now starts just inside four minutes remaining without extending the five-minute evidence lease or increasing rate limits. A timed regression performs 172 paced source reads across two resources, taking more than three minutes but less than four, and requires coverage to stay valid throughout. Graph reauthorization/renewal also uses one PATCH rather than the conflicting POST-plus-PATCH sequence. Coverage checkpoint reasons and lifecycle timestamps are available in Control inspection to distinguish expired proof, real gaps and authentication failures.

Lifecycle recovery is tested separately from ordinary renewal. A confirmed Graph `reauthorizationRequired` signal fences source coverage while repair runs; existing history can remain available only while current authority is valid. Treating that intermediate state as synchronized/healthy would still bypass acceptance. Observation retains it as **recovering**, not passed, only with the exact renewed subscription, handled signal, fresh membership, matching repair generation, recent progress and no blocked/authentication failure. It must then observe all five resources healthy within five minutes of the original signal; retries never move that deadline. Ordinary expired coverage, stale authority, mismatched/expired subscriptions, blocked/stalled repair or late recovery still fail source-health acceptance even when authorized content is visible. The original failed observations remain historical failures, not retroactive passes.

Then, only within separately approved resources/budgets, establish fresh sessions, real private-network data access, exact-pair/source authority, signed notification-to-browser delivery and normal send/read-back. Keep source IDs/provenance distinct from mocked evidence. Do not stress real Graph to test application capacity.

Production G0-G7, authority/DLP completeness, scale, physical retention, soak and regional recovery remain independent acceptance requirements. Historical simulator/GraphPilot records in the validation report/ADRs are retained as history, not current instructions.
