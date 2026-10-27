# Development setup and deployment

## Current status and boundaries

There is one environment: **Development**, connected to real Microsoft Teams. It now runs five isolated Live workloads in Sweden Central: Api, Receiver, Projector, Delivery and Control. The existing API and worker were retained for Api and Projector; the additional apps use `-recv`, `-dlvr` and `-ctrl` suffixes to stay within the Container Apps name limit. Simulator and legacy GraphPilot are no longer supported runtimes; local persona/password sign-in, simulator controls, fixture seeding/reset, and pilot polling have been removed.

The deployed service profile uses `authorityMode=Policy`, `entitlementProvider=Graph` and Control's managed identity federated to the separate sync registration. Alex, Sam and Jordan remain ordinary interactive users; their sessions are not background credentials. Preserve the dedicated group chat/channel, three existing pair mappings and history cutoffs. `DelegatedSynthetic` remains only as guarded migration compatibility until unattended acceptance; it is not the deployed profile. This does not authorize organization-wide discovery, new chat creation from the app, business-content processing, or production acceptance.

The cloud migration was explicitly approved on 2026-10-21. Five app-owned rich subscriptions, all five covered resources and their expected source-backed memberships were verified before user admission was enabled. The admitted public shell, anonymous denial and canonical Entra redirect pass. The eight-hour unattended observation and a fresh authenticated browser journey remain acceptance checks; do not equate healthy revisions or offline browser tests with those results.

Read-only Azure projection acceptance also passed using Api's actual managed identity: three exact catalogs, twelve authorized snapshots, three excluded-pair denials and six existing direct-chat openings. The diagnostic logged no message bodies and sent no messages. This establishes the private storage/access path, not a signed-in browser or delegated-write result.

The selected deployment remains `rg-teams-reference-dev-swc` in `swedencentral`. Historical validation is in [dev-validation.md](dev-validation.md); it is not proof that the current source image has been deployed. No remote deployment or GitHub execution is implied by local verification. Earlier subscription/capacity attempts and their resources remain separate; no cleanup is automatic.

**Availability-first source is not deployed.** Stored authorized history no longer
depends on healthy source coverage; cold views use typed loading and bounded,
durable Control hydration. Missed edits/deletions may remain until reconciliation
or seven-day expiry; current authority/parent/history/epoch checks remain strict.
A combined release needs a rebuilt image, explicit Session settings and separately
approved `graph-read-refresh` queue plus exact queue-scoped API Sender/Control
Receiver grants. See the [release/support runbook](live-runtime.md#availability-first-reads-and-hydration).

## Repository layout

| Path | Responsibility |
| --- | --- |
| `src\Teams.Core` | Live contracts, source-authorized reads, command safety and projection merge rules |
| `src\Teams.Infrastructure` | Cosmos persistence, Redis command budgets and Azure realtime adapters |
| `src\Teams.Host` | Live API/receiver/projector/delivery/control, Entra and encrypted Graph callbacks |
| `src\Web` | Teams-only Split View UI and test-runner-only browser fixtures |
| `tests\Teams.Tests\Support` | Offline test dependencies; not part of the published host |
| `infra\` | Private platform, shared edge, bounded and independent-role Live infrastructure |
| `.github\workflows\` | Offline CI and manual image-only Development release |
| `prototypes\` | Fictional standalone design references, not an application mode |

## Local setup

Use .NET 10, PowerShell 7, Node.js 22.12+ in the Node 22 line, and npm. Restore the existing locked dependencies when setting up a checkout:

```powershell
dotnet restore TeamsReference.slnx --locked-mode
Push-Location src\Web
npm ci
npm run build
Pop-Location
.\scripts\Start-Local.ps1 -ConfigurationPath '<private-directory>\appsettings.local.json'
```

The private JSON uses normal .NET configuration sections (`App`, `AzureAd`, `Azure`, `Callbacks`, and `Live`/`Live:Configuration`). Set `App.GraphMode=Live`, `App.Store=Cosmos`, `App.Auth=Entra`, an approved role, the real tenant, and `App.PublicOrigin=https://localhost:7266`. The launcher accepts a different explicit HTTPS port. `TEAMS_CONFIGURATION_PATH` also lets the host load an absolute private configuration file; environment/command-line overrides are still validated.

A normal local run uses real services. It requires a valid HTTPS developer certificate, an already-approved exact Web redirect `https://localhost:7266/auth/callback`, authorized developer/client credentials, and connectivity/RBAC for the private Azure endpoints. The launcher does not trust certificates, register redirects, grant access, create tunnels, or acquire a managed identity unavailable to the developer. An Azure managed-identity client assertion cannot simply be reused on a laptop. Resolve those prerequisites explicitly, or use the hosted Development app.

Graph notification/lifecycle URLs remain the approved publicly reachable HTTPS endpoints. Do not replace them with localhost, relax token validation, or disable assignment requirements to make a local run start. Do not run competing synchronization/control instances against the shared deployment without reviewing ownership and writer settings. Keep private configuration, credentials, and resolved inventories outside source and browser assets.

For offline work use `.\scripts\Validate.ps1 -Browser`: tests replace dependencies in the test assembly/runner. There is no offline application launcher or local identity bypass. See [testing.md](testing.md).

## Personal-chat test messages

In a verified one-to-one chat, **Send test messages** appears in the conversation header beside **Change person** (or **Change chat**). It opens a popup with **Number of messages** defaulting to **30** and an editable **Test Message** defaulting to **Test message**. Enter a positive whole number, optionally change the text, and choose **Send messages** to send `{Test Message} - 1` through `{Test Message} - N` to the other person through the existing Teams send endpoint. For example, the default starts with `Test message - 1`. Each batch restarts numbering at 1 and leaves your composer draft untouched. The text must be nonblank and fit the 4096-character send limit including the numbered suffix. Group chats and channels do not show this control.

**Concurrent messages** defaults to **1**, independently of the message count: each test message starts after the previous operation finishes. Set a higher concurrency to start that many operations immediately and replace each as it finishes, without pacing or ramp-up. Setting concurrency to 30 lets two users submit sixty initial requests concurrently. Normal manual sends also start immediately; neither path uses a Redis rate gate. The composer clears immediately and remains usable while previous sends await confirmation.

Graph determines throttling. The server retries only explicit Graph `429` message-send rejections: at most three retries after the initial attempt within a two-minute operation deadline by default. It honors `Retry-After`, or uses exponential backoff with jitter when absent. Each operation waits independently; one throttled send does not delay unrelated requests. The browser never resubmits a POST, and ambiguous sends are never automatically replayed. Configure `Live:GraphRetry` as described in [live-runtime.md](live-runtime.md).

Each outgoing bubble has a compact status: a clock while queued/sending, a circled check only after Teams confirms creation (including messages loaded from Teams), and a warning for uncertain/rejected outcomes. A check is a send confirmation, not a read receipt. Errors and **Check original send** remain attached to their own message; never infer success from optimistic display or resend an uncertain operation.

The popup reports confirmed, not-sent, and pending/uncertain counts separately, plus reported initial Graph acceptances/throttles, attempts, retries and total throttle responses. Initial throttles remain counted even if retries eventually succeed. Results are reported as operations finish, not fabricated for requests still in flight. **Close** hides the popup without cancelling the batch; reopen **Send test messages** to inspect progress. **Stop sending**, Escape, leaving the chat, losing connectivity/access, or signing out stops requests not yet submitted. Already-dispatched operations continue to their individual outcomes, including server-side retry handling. With concurrency equal to count, the whole initial burst is already submitted; Stop cannot recall it. Offline drafts are never automatically submitted on reconnect.

These are real Teams messages in normal runs. Only send them to the approved test recipient within the authorized development scope; use offline test fixtures for automated checks.

## Cache Diagnostics

**Cache Diagnostics** appears in the header of an authorized direct or group chat.
It inspects shared application copies, not the original Microsoft Teams messages.
Any signed-in user with current access to that chat may inspect or clean its
copies; channels and all-chat cleanup are excluded.

The popup separates three different things:

- **Browser memory:** loaded messages can remain visible without a fresh storage
  page read. An ordinary update usually fetches Cosmos deltas, not Redis pages.
- **Redis:** at most 21 messages per recent page, cached for up to 15 seconds and
  bounded by message retention. A hit can avoid a Cosmos content-page query, but
  current authorization and conversation metadata still require Cosmos reads.
- **Cosmos:** the shared seven-day message projection. A genuinely cold read
  requests bounded, Control-owned Teams hydration through `graph-read-refresh`;
  accepted work is not evidence that Graph or hydration has already succeeded.

Opening the popup pauses this tab's automatic content reads. **Inspect storage**
collects sampled, timestamped counts, completeness, revision/generation and Redis
TTL evidence without warming Redis or requesting hydration. Other tabs, sends and
background workers continue running. Incomplete counts and unavailable stores are
reported explicitly; a sampled zero is not proof that storage will remain empty.

Neither cleanup option is selected by default. Select Cosmos, Redis, or both,
then confirm **Clean selected caches**. Redis-only cleanup leaves Cosmos content
available. Cosmos cleanup immediately invalidates the old message projection and
Redis page generation, then physically deletes old message documents in bounded
batches. Redis keys remain until expiry unless Redis is also selected.
Conversation identity, permissions, source mappings,
tombstones, delivery records, writer epoch, original send/idempotency status and
composer drafts are preserved. Both selections affect other app users of this
chat; neither selection deletes or changes anything in Teams.

Physical deletion is bounded to four batches of at most 50 messages per request.
**Resume original cleanup** continues larger projections or failed store operations;
**Check original operation** recovers progress after a lost response without
requesting another deletion. The original target revision is retained, so later
synchronization writes are treated as refill rather than deleted again.
Reads do not wait for physical deletion or require resuming it. They ignore
message revisions at or below the invalidation cutoff, then follow the normal
Redis -> Cosmos -> Teams hydration/refill path. Source-confirmed unchanged
messages also receive a new projection revision when refilled. Cleanup completion
does not invalidate that refill or reset the browser again. Sampled Cosmos counts
can include invalidated copies still awaiting physical deletion.
Per-store partial/unknown outcomes are not full success. Content-free operation
records and Redis eviction receipts are retained for fourteen days.
An unfinished operation past that retry window cannot be resumed. Inspect the
current storage and explicitly confirm a new cleanup instead; its expired
active marker does not prevent a replacement operation.

**Reload and validate** deliberately replaces this tab's loaded page and runs the
normal authorized first-page read. Its evidence distinguishes Redis hit/miss,
unavailability, Cosmos content queries, cache writes, and accepted hydration.
Pending hydration uses bounded retries, never a fabricated empty chat. Repeat
the explicit read to observe a warm Redis page if its revision/TTL still permits
it. Observation history holds only the latest twelve count/status observations
in page memory; it does not log message bodies, credentials, or raw cache keys.

The nonblocking cleanup update was deployed to all five Development roles on
2026-10-23 as ACR `dt16`; see the
[release evidence and validation limits](dev-validation.md#nonblocking-cache-cleanup-release-2026-10-23).
No bulk data migration is needed: legacy completed views remain readable, and
unfinished legacy cleanup derives its cutoff from its original reset revision.
Do not mix older writers that omit the new cutoff with the updated runtime.

The earlier [Cache Diagnostics release](dev-validation.md#cache-diagnostics-release-2026-10-23)
is historical `dt15` evidence, not deployment of the nonblocking behavior.
Live normal-read acceptance passed using the unchanged deployed assemblies and
the existing Api identity: 24 reads, including four through an existing unfinished
cleanup, with real Redis misses, Cosmos queries/cache writes and warm Redis hits.
Those four reads returned content despite incomplete source coverage.
Signed-in browser cleanup acceptance was not performed. The stricter source-health
check remains separate and incomplete: a bounded follow-up still observed one
direct chat awaiting reconciliation with Graph `429` responses. Future updates
must retain the coordinated API/worker approval process.
New chat-scoped `recent:v2:` keys do not read old opaque `recent:v1:`
pages; legacy pages expire within their existing maximum 15-second TTL. No Redis
flush, broad key scan, cloud permission change or source-message write is needed.
See the [cache validation procedure](testing.md#cache-diagnostics-validation).

## GitHub Actions

`CI` builds/tests without Azure credentials or Graph writes. **Deploy dev** is a manually dispatched `main` release to the existing GitHub `dev` environment. Configure required reviewers and the main-only policy outside the workflow.

Set these environment variables:

| Variable | Meaning |
| --- | --- |
| `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID` | Explicit existing target boundary |
| `AZURE_RESOURCE_GROUP` | Existing Development resource group |
| `AZURE_REGISTRY_NAME` | Existing ACR; anonymous pull/admin credentials remain disabled |
| `AZURE_DEPLOY_CLIENT_ID` | Approved GitHub OIDC deployment principal |
| `AZURE_API_APP_NAME`, `AZURE_WORKER_APP_NAME` | The existing two Container Apps, not new names |
| `AZURE_CONTAINER_APP_ENVIRONMENT_ID` | Exact existing managed environment resource ID |
| `AZURE_SERVICE_TARGETS_JSON` | After approved service cutover, the reviewed private five-role target contract; omit only while the compact legacy deployment remains active |

The workflow builds and pushes an immutable container digest, preflights every selected target before any application mutation, and updates images only. It preserves Live settings, admission/synchronization state, secrets, identities, networking, sizing, replica limits and all data services. It refuses Simulator/GraphPilot targets and incomplete or inconsistent configuration. It does not call platform Bicep, seed/reset jobs, Entra reconciliation or Graph writes.

With `AZURE_SERVICE_TARGETS_JSON` configured, the workflow preflights all five existing isolated roles through `Deploy-ServiceDev.ps1`. The contract must match the protected tenant/subscription/resource group/environment. It verifies one attached identity per role, distinct Web/sync clients, the exact approved resource/policy configuration, effective Cosmos assignments and the read-only primary-reader role definition. Extra compact API authority-write privileges and incomplete drafts are refused before the first update. The service release preserves all configuration and limits and reports exact partial-rollout/recovery identities; it does not grant access, provision workloads, alter edge routing or enable admission. The two-target path is only for an unmigrated legacy environment and cannot accept the deployed service configuration.

The five-role preflight also reads `graph-read-refresh` properties and requires
exact queue-scoped API Data Sender and Control Data Receiver grants; inherited
broader data grants do not satisfy that boundary. Provision the queue/grants
through a separately approved access change before image mutation.
`New-ServiceDevPlan.ps1` includes this dependency in its approval inventory.
Runtime startup verifies data-plane access; neither check sends a Teams message.
Compact release scripts rely on startup checks rather than this ARM preflight.

## Service identity migration (approval-gated)

The initial scope stays the three assigned test users and five existing resources in the confirmed dedicated test tenant. They remain ordinary users, not background credentials. The service path uses five isolated workloads; the API retains delegated user sends while Control uses the separately federated sync application and the real Graph-backed policy provider.

Resolved inventories and approval artifacts stay outside the repository:

```powershell
.\scripts\New-ServiceDevPlan.ps1 -InventoryPath '<private>\inventory.json' -OutputDirectory '<private>\service-migration'
```

The generator makes no cloud calls. It produces `approval-required.json` and deliberately invalid `service-targets.draft.json`: new identity client/principal IDs and the policy approval reference must be resolved and reviewed before deployment. The diff names the three additional workloads/identities, seven proposed sync read permissions, preserved resource/history mappings, exact existing Cosmos assignments needing retirement and the additional allocation (three roles, each bounded to 0.5 vCPU/1 GiB with explicit replica limits). It does not change the existing spending cap or establish an actual billing quotation.

The staged Bicep entry points have separate responsibilities:

| Template | Boundary |
| --- | --- |
| `infra\service-dev-identities.bicep` | Only the three reviewed missing Receiver/Delivery/Control identities |
| `infra\service-dev-access.bicep` | Explicitly approved role-specific grants and read-refresh queue in the existing namespace; preserve compatible assignment IDs |
| `infra\service-dev.bicep` | Five single-role workloads on the existing environment/platform, with synchronization and admission off by default |
| `infra\service-dev-edge.bicep` | Only the reviewed Graph callback Receiver origin/route on the existing edge; browser and PubSub routes remain unchanged |

Do not apply the old production template unchanged: its edge creation and scaling defaults are not an approved replacement for the existing Dev platform. ARM incremental deployment does not remove old grants. Review and explicitly retire excess compact-role assignments, including the API's authority/control writes, before normal admission; `Deploy-ServiceDev.ps1` rejects excess effective Cosmos access.

Use `deployRoles` to stage only Receiver/Delivery/Control before the controller fence; never replace both existing workloads as an incidental staging side effect. Preserve exact compatible Redis assignment names as well as Cosmos/ARM assignments. Api receives Crypto Service Encryption User (key read/wrap/unwrap) only on its session-protection key, not vault-wide Crypto Officer. After verifying the old revisions inactive, the approved migration retired seven obsolete Cosmos assignments and seven obsolete ARM assignments without deleting source data or operation journals. Callback activation must verify the exact Graph validation echo on both public paths after Front Door propagation, not just ARM provisioning success.

Before any live apply, approve the exact Graph consent, federation/publisher admission, effective RBAC removals/additions, infrastructure/edge changes, cost impact and recovery procedure. `Sync-Identity.ps1` can explicitly admit Graph Change Tracking to an assignment-required sync application. Stored sync password/certificate credentials require separate review. Its `-RetireValidationGrants` switch permits only the recorded validation read/create scopes on the owned Principal grants; `-ApprovePermissionChanges` is still required to apply, and unrelated broad consent remains refused.

After reviewed identity creation, re-inventory actual principal/client IDs, resolve the sync federation subject and target map, and verify the identity profile. Stage workloads disabled; fence the old controller; migrate subscription ownership with journaled service-prefixed scope IDs and current callback audiences/keys. Old delegated subscriptions are not inherited by the sync application. Never reset source data or replay an uncertain create/send. Establish fresh per-user authority and coverage before admission.

Only after all five service workloads and their effective privileges are established, routine image-only preview/apply uses:

```powershell
.\scripts\Deploy-ServiceDev.ps1 -TargetsPath '<private>\service-targets.json' `
    -RegistryName '<existing-registry>' -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>'
# After the separately approved release:
.\scripts\Deploy-ServiceDev.ps1 -TargetsPath '<private>\service-targets.json' `
    -RegistryName '<existing-registry>' -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>' `
    -Apply -Confirmation DEPLOY-DEV -Confirm:$false
```

Verify background operation with the users signed out, token/subscription renewal, policy revocation/history boundaries, repair recovery and role isolation. Real message writes and interactive sign-in helpers remain separate opt-ins. An image rollout or process-readiness response is not unattended evidence. Remove the validation-only runtime and retire its stale credentials/grants only after the service cutover is accepted; do not delete test users or conversations as incidental cleanup.

## Compact legacy release details (pre-cutover only)

Preflight validates timestamps independently of the PowerShell locale and derives canonical Graph paths from the persisted resource IDs when computed `path` fields are absent. A supplied path must still match that mapping. Healthy `RunningAtMaxScale` revisions are accepted without increasing replica limits; stopped, unhealthy and mismatched revisions remain blocked.

Image update payloads omit Azure's read-only container `imageType` and resource `ephemeralStorage` fields. The final observed-state comparison still requires those derived values and all existing writable configuration to remain unchanged.

Enter **`DEPLOY-DEV`** when dispatching. The old `DEPLOY-SYNTHETIC-DEV`, fixture actor variables and overloaded bootstrap boolean are not release inputs. OIDC/RBAC must already be approved and configured; the workflow cannot grant itself permissions. Keep deployment scope restricted to the approved resource group/registry; do not add subscription Owner or directory-administration privileges.

The same release can be previewed locally without mutation:

```powershell
$release = @{
    SubscriptionId = '<approved-subscription-guid>'
    TenantId = '<approved-tenant-guid>'
    ResourceGroup = '<existing-development-group>'
    RegistryName = '<existing-registry>'
    ApiAppName = '<existing-api>'
    WorkerAppName = '<existing-worker>'
    EnvironmentId = '<existing-managed-environment-resource-id>'
    Image = '<registry>.azurecr.io/teams-reference@sha256:<digest>'
}
.\scripts\Deploy-Dev.ps1 @release
# Only after release approval:
.\scripts\Deploy-Dev.ps1 @release -Apply -Confirmation DEPLOY-DEV -Confirm:$false
```

The wrapper verifies exact image/revision readiness and public Live/Entra/anonymous-access contracts. These are read-only smoke checks, not authenticated Teams journey evidence. A partial rollout fails with recovery identities; inspect it and restore known-good Live images through an explicitly approved release. Never recover by resetting data or replaying an uncertain Graph POST.

The Session consistency increment requires a separately reviewed configuration
transition before these image-only releases can resume. New source accepts only
`readConsistency: "Session"`; existing legacy profile names fail preflight.
Update image and private configuration together, preserve the previous pair for
rollback, and coordinate all affected roles/jobs. Follow the
[Session migration runbook](live-runtime.md#session-configuration-migration);
do not weaken preservation guards or change Cosmos account policy.
The availability increment also requires the reviewed queue/scoped grants and a
new combined image; the earlier prepared Session-only image does not contain it.
`service-dev.bicep` remains workloads-only and cannot provision that dependency.

## Platform and first workload deployment

Routine releases must not provision infrastructure. The following is an operator runbook, not authorization to execute it. Directory creation, licences, Teams-resource creation, billable provisioning, identity changes and user admission require their own approvals.

1. Select the tenant/subscription and private inventory. Use `Bootstrap-Dev.ps1` only for the approved owned directory objects; it grants no Teams licences/message consent. Use `Sync-Identity.ps1` for desired-state planning, approved grants/trust and drift verification. See [Entra configuration](entra-configuration.md).
2. Preview `New-DevPlatform.ps1` with the existing inventory. The private platform includes its shared Front Door endpoint/WAF independently of workloads, so the canonical origin and actual identity outputs are available before configuring Live. Approve costs/quotas and `Microsoft.Network/AllowBringYourOwnPublicIpAddress` registration separately. Use pinned Bicep 0.48.1 in CI.
3. Apply the approved scoped `infra\synthetic-live.bicep` storage/queue/key/access definitions against that platform. This name denotes the bounded real-Teams profile. It does not create a simulator or resize services. Preserve the single-region Session contract and the read-only procedure required for critical current reads.
4. Prepare the notification public certificate using the existing versioned Key Vault key and the `PrepareNotification` diagnostic under explicitly authorized real-service credentials. This operation can run before normal host startup, with `App:GraphMode=Live`, `App:Operation=PrepareNotification`, `LivePreparation:KeyUri`, `VaultName`, and `ApprovalReference`. No private key is exported. Keep encryption material and references private; never replace verification with test keys.
5. Prepare explicit approved Live resource inventory/configuration, real callback URLs, history cutoffs, actors, owners, consent and notification-publisher assignment. Reuse existing owned Teams resources. `New-DevTeamsResources.ps1` can prepare a separately approved dedicated group/team/channel with checkpointed creation; it is not part of release or normal startup. Existing one-to-one mappings must be supplied explicitly; application discovery and `EnsurePairs` verify/reuse them, not create them.
6. Preview `infra\main.bicep` with `deployWorkloads=true`, immutable `image` and reviewed `liveConfig` for the compact Development topology. Leave synchronization/admission explicitly off until their prerequisites are approved. Its API hosts Api/Receiver/Control; its worker hosts Projector/Delivery. `deployOperations=true` optionally provisions a **manual Live diagnostic job**, retaining the historical `-seed` name where needed; it never seeds/reset conversations. No retired runtime must be deployed first.
7. Complete/verify actual origin, FIC principal/client IDs, callback principal and private-origin approval; establish the approved user sessions, synchronization, subscription/coverage/authority readiness and then admission through separate configuration approvals. For configuration changes to the existing two apps, use `Deploy-SyntheticLive.ps1` with reviewed private settings. Do not use the five-app Policy template to replace the existing topology unintentionally.
8. Once the current five-resource Live scope and three approved existing pair mappings are stable, `New-SyntheticLiveSettings.ps1` exports current metadata/snapshots privately. Supply `PrivateDirectory`, `SubscriptionId`, `ResourceGroup`, `ApiAppName`, `WorkerAppName`, and the explicit existing `OperationJobName`. Optional `ScopePath` supplies approved complete Live configuration, not an archived pilot snapshot. The full-journey helper metadata deliberately requires all five existing resources; it is not used to invent missing mappings. Normal releases subsequently use `Deploy-Dev.ps1`.

Use incremental, reviewed provisioning; removal of old template definitions is not permission to delete old resources. The retired `state` container/plaintext queue and old job revisions may remain in Azure until an owner reviews retention, dependencies and deletion scope. A manual job reused for Live diagnostics is not an unused simulator asset merely because its historical name contains `seed`.

The private topology has significant fixed costs (Front Door Premium, Service Bus Premium, Redis, PubSub, private endpoints and other services). Check current Sweden Central prices, quotas and actual what-if results; historical capacity attempts or compiled templates do not establish deployability or cost approval.

## Development Service Bus Standard cutover

The Development PoC can replace Service Bus Premium with a **new Standard
namespace** using public TLS and the existing Entra managed identities. This is
not an in-place SKU downgrade. All other services, private endpoints, Front Door
origins and the five isolated roles remain unchanged. Public access is an
intentional Development exception, not a production recommendation. Local/SAS
authentication stays disabled; no new NAT gateway or assumed stable-egress IP
allowlist is introduced.

Use `infra\service-dev-bus.bicep` with a private parameters file containing the new
`namespaceName`, existing five `principals` and `provisionAccess: true`. It creates
only the Standard namespace, `graph-notifications`, `graph-delivery`,
`graph-read-refresh` and their queue-scoped grants. It does not recreate the
historical `notifications` queue. Review the scoped preview before applying:

```powershell
$subscription = '<approved-subscription-guid>'
$group = '<existing-development-group>'
$parameters = '<private>\bus.parameters.json'
az deployment group what-if --subscription $subscription --resource-group $group `
    --template-file infra\service-dev-bus.bicep --parameters "@$parameters"
# After provisioning/public-endpoint/access approval:
az deployment group create --subscription $subscription --resource-group $group `
    --name service-dev-standard-bus --template-file infra\service-dev-bus.bicep `
    --parameters "@$parameters" --output none
```

For the simple PoC interruption, save current app/job definitions privately,
pause traffic and producers, and confirm the old namespace has no unaccounted
active, deferred, locked, scheduled, transfer or dead-letter work. Do not purge
unexpected messages. Switch only `Azure__ServiceBus` on the five existing apps
and the manual diagnostic job to `<new-name>.servicebus.windows.net`. Preserve
their immutable images, identities, other environment settings, scale, callback
bindings, subscription journals and Cosmos checkpoints. Verify exact healthy
revisions, all queue contracts and actual role-specific access before retiring
the old namespace. No test Teams message is sent by default.

For Single-mode Container Apps, use the supported resource `stop`/`start`
operations rather than changing revision mode or replica limits:

```powershell
# $appId comes from the reviewed current app inventory.
az rest --subscription $subscription --method post `
    --url "https://management.azure.com${appId}/stop?api-version=2025-07-01" `
    --output none
# Wait for runningStatus=Stopped before patching the saved template.
# After all five endpoints and the idle manual job are updated:
az rest --subscription $subscription --method post `
    --url "https://management.azure.com${appId}/start?api-version=2025-07-01" `
    --output none
```

Stop Api, Receiver and Control producers first, let Projector/Delivery drain,
then stop those consumers too. With all workloads stopped and no job execution
running, wait beyond the one-minute queue lock duration and recheck every queue's
total and detailed counts. Resume consumers before producers. Compare complete
preserved configurations and exact healthy revisions, not just a successful
update response. Keep recovery snapshots and migration evidence private.

Persist `serviceBusName` in the reviewed private service-targets contract and, if
configured, the protected `AZURE_SERVICE_TARGETS_JSON` release input. Image-only
releases require every workload's endpoint to match this contract. Historical
service-target files need the explicit field added from verified inventory;
agreement among apps alone is not sufficient. `New-ServiceDevPlan.ps1` records the
inventoried namespace; when two namespaces exist, select it explicitly with
`-ServiceBusName`.

For future platform previews, pass `-ExistingServiceBusName '<new-name>'` to
`New-DevPlatform.ps1`, `existingServiceBusName` to the platform parameters, or
`TEAMS_SERVICE_BUS_NAME` when using `infra\parameters\dev.bicepparam`. Selecting
the existing namespace skips provisioning the default Premium namespace and
its private endpoint. Omitting the selection retains the historical Premium
bootstrap path. Do not broadly redeploy the old compact workload template over
the five-role platform as part of this change.

Standard limits each **encoded message and batch to 256 KB**, including broker
properties and the base64-encrypted notification envelope. Batches are
SDK-negotiated; a single oversized notification fails durable acceptance with
`notification_too_large` rather than being truncated or acknowledged. Graph may
retry it; an oversize event can still require authoritative repair. This PoC
accepts the lower transport ceiling and variable shared-tier latency; it does not
add payload offloading. Watch oversize errors, throttling, backlog and DLQs.

Before retirement, rollback restores the saved endpoints/revisions only after
accounting for any work accepted by Standard. Deleting Premium removes that
rollback option. Obtain separate approval to remove the exact old namespace
and its obsolete Service Bus private endpoint; leave shared private DNS zones
and other services untouched. **Premium continues charging until deleted.**

## Validation, retention and cleanup

Use `Test-DeployedApp.ps1 -PublicOrigin ... -TenantId ... -WebClientId ... -ExpectedMode Live -ExpectAdmission` for an admitted API, omitting `-ExpectAdmission` only for a deliberately fenced rollout. It checks the shell/assets, Live API readiness, anonymous denial, absence of local sign-in and canonical Entra redirect. No message is sent.

Authenticated checks and real Teams posts are separate opt-ins documented in [testing.md](testing.md) and [live-runtime.md](live-runtime.md). Logs/artifacts must exclude bodies, tokens, credentials, capability URLs and raw authentication traces.

Keep retention, operation evidence, authority, coverage, subscription state, keys, delivery checkpoints and session compatibility intact during releases. Production G0-G7, capacity, soak, regional recovery and business-data acceptance remain independent. For teardown, inspect exact ownership and obtain separate approval for each deletion; do not delete a resource group or force-purge Key Vault as part of this code cleanup.
