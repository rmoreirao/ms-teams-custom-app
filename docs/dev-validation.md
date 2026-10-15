# Dev functional validation report

**Outcome: local simulator-backed implementation validated; Azure/Entra rollout is blocked, not complete.**

| Context | Value |
| --- | --- |
| Validation date | 2026-10-15 |
| Source | Working tree based on `fa09e7f`; implementation changes are uncommitted and not pushed |
| Environment | Windows, .NET SDK 10.0.402, Node 22.14.0, npm 10.9.2, installed Edge |
| Dataset | `split-view-v1`, six conversations, four authenticated synthetic personas |
| Authentication exercised | Real application cookies/CSRF with explicit local credentials; not Entra sign-in |
| Persistence exercised | Local in-memory adapter; Azure adapter behavior is not certified |

## Checks executed

| Check | Result | Evidence / boundary |
| --- | --- | --- |
| Backend/domain/API/Graph-transport contracts | **28 passed** | xUnit, including authenticated HTTP/CSRF checks; Graph HTTP responses are controlled test responses, not actual Graph calls |
| React component tests | **2 passed** | Unknown sends offer original-operation checks; simulator confirmation is explicitly not Teams delivery |
| Browser scenarios | **8 passed** | Running Web App in Edge, not isolated UI mocks |
| TypeScript and production frontend build | **Passed** | Vite build and strict type checking |
| Locked .NET restore and Release publish | **Passed** | Published frontend `wwwroot` assets verified present |
| Bicep | **Compiled** | Main/subscription/modules with Bicep 0.48.1; no successful Azure deployment or quota proof |
| GitHub Actions | **Lint passed** | actionlint 1.7.7; remote workflow not executed |
| PowerShell management scripts | **Syntax parse passed** | Directory/deployment scripts not executed against remote resources |
| UI mechanical detector | **No deterministic findings** | Not an accessibility certification or performance result |
| Azure ARM preflight | **Blocked** | `ReadOnlyDisabledSubscription`; subsequent read returned `Disabled` |
| OCI container build | **Not run** | Local Docker Linux engine unavailable; .NET publish is not a container-build substitute |
| Load testing / regional recovery | **Excluded by request** | No tooling, execution, capacity result, or load report |

Backend evidence covers command replay/payload conflicts, concurrent duplicate submission while the budget is occupied, conditional expiry of an undispatched claim, unknown receipt resolution, ownership checks, history/parent visibility, revocation/offboarding, equal-timestamp pagination, coverage gaps, tombstones/out-of-order changes, fixture reset epochs, serialized projection bounds, and live HTTP send outcome classification without POST retries.

The simulator restart test creates a new simulator instance against the same store. It does not claim that the local memory store survives a server-process restart or that Cosmos/broker crash recovery has been demonstrated.

Final browser validation exposed shared-actor rate-budget coupling between cases. The harness now waits for actual send responses, distinguishes confirmed content from optimistic rendering, and respects the unchanged 1.25-second sender budget between cases. The final complete eight-case run passed with no retries or limiter relaxation.

## Browser journeys

1. Browse and page conversations, preserve an in-memory draft across navigation, send once, and sign out.
2. Navigate channel threads, send a reply, receive a queued external update, and render hostile markup as text rather than HTML.
3. Lose send confirmation, block automatic resend, check the original operation, and show exactly one created message.
4. Sign in as the restricted persona and deny both discovery and direct access to another conversation.
5. Offboard an already signed-in persona, deny its existing session, close its local real-time connection, and clear its open view.
6. Reject a send without creating a message; disable sending offline while retaining only the open-page draft; restore sending availability online.
7. Apply external edits/deletions through the inbox/worker/hint/refetch path.
8. Exercise desktop/mobile Split View navigation, themes and wrapping, and verify zero localStorage/sessionStorage entries for the test application.

The suite starts/stops its own localhost host. No long-lived dev server was intentionally left running. Local SSE exercises application hint/refetch logic, not Azure Web PubSub's protocol, identity, network rules, admission, or connection capacity.

## Visual evidence

These screenshots contain generated test content only.

![Desktop Split View](validation/split-view-desktop.png)

[Mobile dark-theme Split View](validation/split-view-mobile.png)

Local detailed outputs are generated under `tests\Teams.Tests\TestResults`, `src\Web\playwright-report`, and `src\Web\test-results` and are ignored by Git. CI publishes only backend results and screenshots; traces are off by default and HTML credential-entry reports are not uploaded.

## Blocking prerequisites and remaining scope

| Gate | Current state |
| --- | --- |
| Enabled Azure target | Selected subscription confirmed disabled. Re-enablement or an explicitly approved replacement is required. |
| Directory/provisioning approval | Requested, but user unavailable. No new Entra app registrations or real test accounts were created. |
| Teams licensing | Approved tenant returned no subscribed Microsoft 365 licence SKUs. No licence was purchased or trial activated. |
| First cloud deployment | No resources/images were deployed and no Azure fixture job ran. Private networking, managed identity, Redis/Cosmos/Service Bus/PubSub behavior, actual purge, and cloud telemetry remain unverified. |
| Git publication and remote CI/CD | No branch changes, commits, pushes, or remote workflow execution. Environment protection/OIDC/RBAC setup remains a bootstrap action. |
| Live Teams implementation/readiness | Runtime intentionally rejects live mode. Resource onboarding, current Graph entitlement/policy authority, rich callback/subscription/repair wiring and real-tenant validation remain unfinished. The isolated delegated-send transport is not a complete Teams integration. |

This evidence does **not** pass production gates G0-G7, demonstrate a 200,000-user envelope, prove a real Graph send/freshness SLO, certify DLP/history/offboarding equivalence, or authorize processing existing business data. The approved synthetic dev scope and remaining work are recorded in [ADR 0008](adr/0008-simulator-first-single-region-development.md).

## Reproduction

Use [test guidance](testing.md), [dev setup/deployment](dev-guide.md), and the versioned fixture manifest. For a fresh local environment:

```powershell
.\scripts\Validate.ps1 -Browser
```

Resolve the explicit cloud/identity prerequisites before attempting deployed browser validation. Do not replace missing evidence with simulator success or open private endpoints to make a deployment pass.
