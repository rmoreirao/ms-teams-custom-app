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
| Missing coverage | Preparing/error rather than fabricated empty content; bounded safe-read recovery |
| Direct chats | Existing approved exact-pair mapping only; stale/changed membership denied; no creation/send on selection |
| Personal-chat test messages | Header action beside Change person in verified one-to-one chats only; popup defaults to 30 and editable text "Test message"; exact numbered bodies, distinct operations, paced overlapping requests, preserved drafts, explicit outcome counts, continuation without retries, and cancellation of remaining sends |
| Realtime/reconnect | Web PubSub and targeted changes; no 30-second catalog polling or SSE fallback |
| UX and safety | Desktop/mobile focus/navigation, drafts in memory, offline send disabled, no business browser storage, sanitized rich content, virtualization |
| Release | Both targets preflighted before mutation; image-only preservation; exact revision readiness; explicit partial-rollout errors |

`MessagingTests` now exercises actual Live persistence and controlled Graph outcomes instead of a stateful simulator. `LiveDiscoveryTests` covers authorized existing-pair discovery and no creation. `LiveJourneyTests` traverses intake, authenticated decryption, projection, durable outbox, leased delivery and catch-up with controlled dependencies.

`LiveControlTests`, `LiveStateTests`, `LiveGraphTests`, `OutboxDeliveryTests`, `PrimaryReadTests`, `BoundedJsonTests`, `PubSubAdmissionTests`, `OidcCallbackTests`, and `LiveTelemetryTests` retain their specific correctness boundaries. The opt-in real Cosmos probe stays skipped unless explicitly configured; offline tests never silently use real services.

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

`--teams` sends one chat message, one channel post and one reply for the selected account. `--direct-chat` reuses the approved pair and sends exactly one message; it does not create a chat. The Azure event-path runner uses `AZURE_LIVE_SETTINGS`, approved live authentication metadata and Probe/VerifyProbe to distinguish external Graph creation from actual notification/projection/delivery evidence. Never replay an unresolved source probe.

`EnsurePairs` now verifies the three configured existing exact-pair mappings and preserves each approved history cutoff. It no longer uses the retired pilot store or creates Teams chats.

## Infrastructure and release evidence

Compile `infra\main.bicep`, `subscription.bicep`, `live.bicep` and `synthetic-live.bicep` with the pinned Bicep toolchain. Compilation is not real what-if or cloud acceptance. Actual provisioning, directory changes, workflow dispatch, image push and user admission are separate approved operations.

After an authorized release, read-only smoke must establish the exact new API/worker ready revisions, Live mode, expected admission, shell/assets, anonymous denial and canonical Entra redirect. A passing local suite does not establish real Graph integration for a new image.

Then, only within separately approved resources/budgets, establish fresh sessions, real private-network data access, exact-pair/source authority, signed notification-to-browser delivery and normal send/read-back. Keep source IDs/provenance distinct from mocked evidence. Do not stress real Graph to test application capacity.

Production G0-G7, authority/DLP completeness, scale, physical retention, soak and regional recovery remain independent acceptance requirements. Historical simulator/GraphPilot records in the validation report/ADRs are retained as history, not current instructions.
