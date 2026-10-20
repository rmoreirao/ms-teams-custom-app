# Development setup and deployment

## Current status and boundaries

There is one environment: **Development**, connected to real Microsoft Teams. Its existing Sweden Central API and worker host five logical Live roles. Simulator and legacy GraphPilot are no longer supported runtimes; local persona/password sign-in, simulator controls, fixture seeding/reset, and pilot polling have been removed.

The existing `DelegatedSynthetic` authority profile is **real Graph integration restricted to approved test users and resources**, not a simulator. Keep Alex, Sam and Jordan, the dedicated group chat/channel, the three existing pair mappings, history cutoffs, current consent, and all identity bindings unchanged. This does not authorize organization-wide discovery, new chat creation from the app, business-content processing, or production acceptance.

The selected deployment remains `rg-teams-reference-dev-swc` in `swedencentral`. Historical validation is in [dev-validation.md](dev-validation.md); it is not proof that the current source image has been deployed. No remote deployment or GitHub execution is implied by local verification. Earlier subscription/capacity attempts and their resources remain separate; no cleanup is automatic.

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

The batch starts messages every 1.5 seconds without waiting for earlier confirmations. All sends share an in-memory per-session pacer to respect the server's budget, including rapid manual sends and sends from different conversations. The composer clears immediately and remains usable while previous sends await confirmation; a pending or uncertain message does not block a different new message. There is no automatic retry.

Each outgoing bubble has a compact status: a clock while queued/sending, a circled check only after Teams confirms creation (including messages loaded from Teams), and a warning for uncertain/rejected outcomes. A check is a send confirmation, not a read receipt. Errors and **Check original send** remain attached to their own message; never infer success from optimistic display or resend an uncertain operation.

The popup reports confirmed, not-sent, and pending/uncertain counts separately. **Close** hides it without cancelling the active batch; reopen **Send test messages** to inspect its progress. **Stop sending**, Escape while the popup is open, leaving the chat, losing connectivity/access, or signing out stops remaining batch requests; requests already in flight still complete and retain their individual outcomes. Queued manual requests are cancelled explicitly on connection/session loss and are never persisted or automatically resumed.

These are real Teams messages in normal runs. Only send them to the approved test recipient within the authorized development scope; use offline test fixtures for automated checks.

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

The workflow builds and pushes an immutable container digest, checks both apps before any application mutation, and updates their images only. It preserves Live settings, admission/synchronization state, secrets, identities, networking, sizing, replica limits and all data services. It refuses Simulator/GraphPilot targets and incomplete or inconsistent configuration. It does not call platform Bicep, seed/reset jobs, Entra reconciliation or Graph writes.

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

## Platform and first workload deployment

Routine releases must not provision infrastructure. The following is an operator runbook, not authorization to execute it. Directory creation, licences, Teams-resource creation, billable provisioning, identity changes and user admission require their own approvals.

1. Select the tenant/subscription and private inventory. Use `Bootstrap-Dev.ps1` only for the approved owned directory objects; it grants no Teams licences/message consent. Use `Sync-Identity.ps1` for desired-state planning, approved grants/trust and drift verification. See [Entra configuration](entra-configuration.md).
2. Preview `New-DevPlatform.ps1` with the existing inventory. The private platform includes its shared Front Door endpoint/WAF independently of workloads, so the canonical origin and actual identity outputs are available before configuring Live. Approve costs/quotas and `Microsoft.Network/AllowBringYourOwnPublicIpAddress` registration separately. Use pinned Bicep 0.48.1 in CI.
3. Apply the approved scoped `infra\synthetic-live.bicep` storage/queue/key/access definitions against that platform. This name denotes the bounded real-Teams profile. It does not create a simulator or resize services. Preserve the single-region SessionPrimary contract and read-only primary-read procedure.
4. Prepare the notification public certificate using the existing versioned Key Vault key and the `PrepareNotification` diagnostic under explicitly authorized real-service credentials. This operation can run before normal host startup, with `App:GraphMode=Live`, `App:Operation=PrepareNotification`, `LivePreparation:KeyUri`, `VaultName`, and `ApprovalReference`. No private key is exported. Keep encryption material and references private; never replace verification with test keys.
5. Prepare explicit approved Live resource inventory/configuration, real callback URLs, history cutoffs, actors, owners, consent and notification-publisher assignment. Reuse existing owned Teams resources. `New-DevTeamsResources.ps1` can prepare a separately approved dedicated group/team/channel with checkpointed creation; it is not part of release or normal startup. Existing one-to-one mappings must be supplied explicitly; application discovery and `EnsurePairs` verify/reuse them, not create them.
6. Preview `infra\main.bicep` with `deployWorkloads=true`, immutable `image` and reviewed `liveConfig` for the compact Development topology. Leave synchronization/admission explicitly off until their prerequisites are approved. Its API hosts Api/Receiver/Control; its worker hosts Projector/Delivery. `deployOperations=true` optionally provisions a **manual Live diagnostic job**, retaining the historical `-seed` name where needed; it never seeds/reset conversations. No retired runtime must be deployed first.
7. Complete/verify actual origin, FIC principal/client IDs, callback principal and private-origin approval; establish the approved user sessions, synchronization, subscription/coverage/authority readiness and then admission through separate configuration approvals. For configuration changes to the existing two apps, use `Deploy-SyntheticLive.ps1` with reviewed private settings. Do not use the five-app Policy template to replace the existing topology unintentionally.
8. Once the current five-resource Live scope and three approved existing pair mappings are stable, `New-SyntheticLiveSettings.ps1` exports current metadata/snapshots privately. Supply `PrivateDirectory`, `SubscriptionId`, `ResourceGroup`, `ApiAppName`, `WorkerAppName`, and the explicit existing `OperationJobName`. Optional `ScopePath` supplies approved complete Live configuration, not an archived pilot snapshot. The full-journey helper metadata deliberately requires all five existing resources; it is not used to invent missing mappings. Normal releases subsequently use `Deploy-Dev.ps1`.

Use incremental, reviewed provisioning; removal of old template definitions is not permission to delete old resources. The retired `state` container/plaintext queue and old job revisions may remain in Azure until an owner reviews retention, dependencies and deletion scope. A manual job reused for Live diagnostics is not an unused simulator asset merely because its historical name contains `seed`.

The private topology has significant fixed costs (Front Door Premium, Service Bus Premium, Redis, PubSub, private endpoints and other services). Check current Sweden Central prices, quotas and actual what-if results; historical capacity attempts or compiled templates do not establish deployability or cost approval.

## Validation, retention and cleanup

Use `Test-DeployedApp.ps1 -PublicOrigin ... -TenantId ... -WebClientId ... -ExpectedMode Live -ExpectAdmission` for an admitted API, omitting `-ExpectAdmission` only for a deliberately fenced rollout. It checks the shell/assets, Live API readiness, anonymous denial, absence of local sign-in and canonical Entra redirect. No message is sent.

Authenticated checks and real Teams posts are separate opt-ins documented in [testing.md](testing.md) and [live-runtime.md](live-runtime.md). Logs/artifacts must exclude bodies, tokens, credentials, capability URLs and raw authentication traces.

Keep retention, operation evidence, authority, coverage, subscription state, keys, delivery checkpoints and session compatibility intact during releases. Production G0-G7, capacity, soak, regional recovery and business-data acceptance remain independent. For teardown, inspect exact ownership and obtain separate approval for each deletion; do not delete a resource group or force-purge Key Vault as part of this code cleanup.
