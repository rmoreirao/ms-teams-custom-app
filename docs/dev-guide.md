# Dev setup and deployment

## Current status and boundaries

This is a simulator-first dev application, not a completed Teams integration. The local app is executable. Bicep and workflow definitions are supplied, but no Azure resources, Entra registrations/users, or remote workflow runs have been created by this implementation session: approval was requested and the user was unavailable.

Azure ARM preflight additionally returned `ReadOnlyDisabledSubscription`; a direct subscription read confirmed `state: Disabled`. Re-enable the selected subscription or explicitly approve another target before deployment. Do not silently switch subscriptions or tenants. The failed preflight used synthetic placeholder IDs and created no resources; it was not a successful environment-specific what-if.

The approved topology is one West Europe environment with Front Door Premium/WAF, private application origins/data services, and authenticated public ACR. No regional recovery, load-test tooling, load run, or load report is included. See [ADR 0008](adr/0008-simulator-first-single-region-development.md).

The deployment retry encountered a subscription restriction on Cosmos allocation in West Europe and unavailable Redis B0 capacity. Its dev fallback keeps compute/network in West Europe, places only synthetic Cosmos data in North Europe over Private Link, and uses Redis B1 in West Europe. This is not regional recovery. Retain these explicit settings on subsequent releases; do not silently revert data locations. Key Vault public access remains disabled, with the trusted Azure-services exception enabled for ARM key provisioning; ordinary runtime access still uses private networking and RBAC.

## Repository layout

| Path | Responsibility |
| --- | --- |
| `src\Teams.Core` | Contracts, authorization, bounded reads, command safety, projection merge rules |
| `src\Teams.Infrastructure` | Memory/Cosmos stores, stateful simulator, budgets, inbox, real-time adapters, fixtures |
| `src\Teams.Host` | BFF/API, worker/maintenance entry points, Entra/session setup, gated live-send transport |
| `src\Web` | React Split View frontend, component and Playwright tests |
| `infra\` | Subscription/resource-group Bicep and private network/workload modules |
| `scripts\entra\` | Explicit, repeatable directory bootstrap and final identity bindings |
| `scripts\test-data\` | Fixture manifest and in-network seed/reset invocation |
| `.github\workflows\` | CI and approval-protected dev release definitions |

## Local setup

Use .NET 10 SDK, PowerShell 7, and Node 22.12+ in the Node 22 line. Dependencies are pinned by lockfiles. NuGet uses Microsoft's public `dotnet-public` mirror; direct nuget.org TLS failed on the implementation host. This configuration is repository-local and does not modify user-wide sources.

The npm lockfile resolves Microsoft's `1es-public` npm mirror. Anonymous tarball access was verified without credentials; no feed tokens are checked in. Direct public npm metadata access was not responsive on this host, so the working public mirror lockfile was retained rather than disabling TLS checks.

```powershell
Push-Location src\Web
npm ci
npm run build
Pop-Location
.\scripts\Start-Local.ps1
```

The script prompts for a temporary password and exports only its salted PBKDF2 hash to the child app. Open `http://localhost:5255`; select **Local development sign-in**, a test persona, and that password. There is no default password or anonymous fixture-login endpoint.

Local credentials, combined hosting, and memory persistence are rejected outside Development/Testing. Local messages live in server memory until process exit; drafts live only in the open browser page. The local SSE transport is not Azure Web PubSub evidence.

See [testing guidance](testing.md) for correctness commands. Build the frontend before publishing/running browser tests. Vite owns fingerprints; ASP.NET serves/publishes physical `wwwroot` assets instead of also generating a competing static-assets manifest.

## Approval and cost gate

Before execution, approve directory creation and billable Azure provisioning separately. Do not buy a licence, start a billing trial, grant tenant-wide message permissions, or publish Git changes automatically.

The selected private topology has substantial fixed cost even at low dev traffic. The Azure Retail Prices API snapshot returned **Front Door Premium base fees of EUR 290.39/month in Zone 1** and **Service Bus Premium at approximately EUR 0.82 per messaging-unit hour in West Europe**. At 730 unit-hours, those two items alone are about EUR 889/month, before Redis, PubSub, ACR, private endpoints, compute, Cosmos, logs, traffic, taxes, and contract adjustments. These are public retail planning figures, not a quote or a spending cap.

Verify current rates, quotas, subscription policies, and West Europe availability. Run the actual what-if using approved identity IDs before applying. A compiled template is not an Azure what-if or proof that a SKU is deployable.

- [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices)
- [Front Door pricing](https://azure.microsoft.com/pricing/details/frontdoor/)
- [Service Bus pricing](https://azure.microsoft.com/pricing/details/service-bus/)

## Entra bootstrap

Use the explicitly approved tenant/subscription, not whatever happens to be the CLI default. Keep the generated inventory outside the public repository.

```powershell
$tenant = '<approved-tenant-guid>'
$subscription = '<approved-subscription-guid>'
$inventory = Join-Path $env:USERPROFILE '.teams-reference-dev\identity.json'
az login --tenant $tenant
.\scripts\entra\Bootstrap-Dev.ps1 -TenantId $tenant -InventoryPath $inventory
# After directory-change approval:
.\scripts\entra\Bootstrap-Dev.ps1 -TenantId $tenant -InventoryPath $inventory -CreateTestUsers -Apply
```

The bootstrap creates/reuses tagged `web`, `sync`, `events`, and `deploy` registrations/service principals, and optionally four dedicated unlicensed users. It grants no Graph message permissions or licences. User creation/role assignments may require an authorized directory administrator; stop on permission errors rather than substituting another tenant or identity.

The web service principal requires assignment, with only the fixture users assigned by bootstrap. The runtime independently checks the fixture identity allowlist before issuing a BFF session; an arbitrary user from the same tenant is not admitted to this synthetic-only app.

Initial user passwords are stored as Windows-DPAPI-protected credential XML beside the private inventory, require change at first sign-in, and never enter source or ordinary logs. An authorized administrator can recover a credential locally with `Import-Clixml`; do not print it into shared transcripts. MFA/Conditional Access and initial password-change interactions remain human steps.

| Identity | Initial purpose |
| --- | --- |
| web | Single-tenant confidential OIDC BFF; managed-identity federation after platform creation |
| sync | Reserved separate background Graph identity; no message permissions granted in simulator delivery |
| events | Explicit callback audience and `PubSub.Callback` application role for the designated PubSub identity |
| deploy | GitHub OIDC subject `repo:<owner>/<repo>:environment:dev`; no directory administration |

## Platform and first workload deployment

Install Bicep 0.48.1 or newer. The implementation host's older 0.28 compiler lacked current resource schemas; validation used a session-local 0.48.1 binary without replacing global tools.

```powershell
.\scripts\New-DevPlatform.ps1 -SubscriptionId $subscription -InventoryPath $inventory
# Review the actual what-if, cost and RBAC changes; then, after approval:
.\scripts\New-DevPlatform.ps1 -SubscriptionId $subscription -InventoryPath $inventory -Apply
```

For the capacity-constrained dev instance, add `-CosmosLocation northeurope -RedisSku Balanced_B1` to both the what-if and apply commands, and include `cosmosLocation`/`redisSku` in the private workload parameter file. Other environments may retain the West Europe/B0 defaults after checking availability.

Initial platform deployment needs resource-group creation and scoped role-assignment rights. It creates identities and data services before application traffic. It does not deploy workload images. Routine releases pass `provisionRbac=false` and do not need Azure role-assignment or Entra administration rights.

Create a private ARM parameter file from the inventory: `webClientId`, `eventsClientId`, and `fixtureActorIds` in manifest persona order. The `infra\parameters\dev.bicepparam` environment-variable template is another option.

Build the image with Docker, push to the authenticated ACR, and obtain its repository manifest digest. ACR Tasks can perform the build when a local Docker engine is unavailable; review its cost/permissions first. Do not pass credentials in build arguments or copy private inventories into build context.

```powershell
.\scripts\Deploy-Dev.ps1 -SubscriptionId $subscription -ResourceGroup rg-teams-reference-dev `
  -ParametersPath '<private-parameters.json>' -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>'
# Initial rollout, after approval; admission remains disabled:
.\scripts\Deploy-Dev.ps1 -SubscriptionId $subscription -ResourceGroup rg-teams-reference-dev `
  -ParametersPath '<private-parameters.json>' -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>' -Apply
```

Obtain the Front Door origin, API identity principal, and PubSub principal from the deployment outputs. After the corresponding directory approval:

```powershell
.\scripts\entra\Complete-Dev.ps1 -InventoryPath $inventory -PublicOrigin '<https-origin>' `
  -ApiIdentityPrincipalId '<api-principal-guid>' -PubSubPrincipalId '<pubsub-principal-guid>'
```

Inspect and approve the Front Door-created private origin connection in the Container Apps environment. Do not disable public-network restrictions as a workaround. Complete DNS/private endpoint checks and verify the private service paths from the in-network jobs.

Rerun `Deploy-Dev.ps1` with `-Apply -EnableRealtimeAdmission`. This installs the authenticated connect handler before enabling connection negotiation, seeds/verifies the allowlisted fixture dataset, and checks Front Door readiness. Existing templates enforce only simulator mode.

## GitHub Actions

CI builds/tests the app, runs Playwright scenarios, compiles Bicep, parses PowerShell, and uploads sanitized local-test evidence. It receives no deployment credentials.

Before enabling `Deploy dev`, create the GitHub `dev` environment with a required reviewer and main-only deployment policy. Grant the deployment principal resource-group Contributor and registry-scoped AcrPush only after approving bootstrap. Do not grant subscription Owner or directory administration to the workflow.

Set environment variables: `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_DEPLOY_CLIENT_ID`, `AZURE_REGISTRY_NAME`, `TEAMS_WEB_CLIENT_ID`, `TEAMS_EVENTS_CLIENT_ID`, `TEAMS_FIXTURE_ACTOR_IDS` (four-element JSON array), and `TEAMS_BOOTSTRAP_COMPLETE=true` only after final bindings/private-origin readiness.

Also set `AZURE_COSMOS_LOCATION=northeurope` and `AZURE_REDIS_SKU=Balanced_B1` for this dev instance so CI/CD preserves its capacity fallback.

The manual workflow requires `DEPLOY-SYNTHETIC-DEV`, environment approval, successful correctness checks, and Azure OIDC. It builds/pushes an immutable image and invokes the same release scripts. Workflow definition/linting does not prove a remote run. Commit/push and remote execution need explicit user authorization.

## Operations, retention, and cleanup

Application logs omit bodies, tokens, credentials, and OAuth query values. Azure Monitor instrumentation is enabled only with its connection setting; SDK PII logging is not enabled. Readiness checks fixture existence and Redis reachability, not production SLOs.

| Copy | Dev lifecycle |
| --- | --- |
| Message projection | Seven days from original creation, at most 200 records and 1 MB serialized message budget per conversation; reads also enforce age |
| Simulator receipts | Cosmos TTL seven days; exact synthetic confirmation evidence, not actual Graph receipts |
| Operation evidence | Body-free records, Cosmos TTL fourteen days from last modification; command windows expire after five minutes |
| Tombstones | Fourteen days, bounded dev safety limit; expired records pruned by worker |
| Broker/DLQ | One-hour message TTL; worker explicitly purges aged DLQ content, at most 100 examined per maintenance pass |
| BFF session/token cache | Protected server-side; absolute session/token-cache lifetime eight hours; Redis persistence disabled |
| Key ring | Protected with Key Vault; loss of Redis may require sign-in, never fallback authorization |
| Principal revocation | Explicit durable deny until an approved restore/reset or environment teardown |
| Telemetry | Body-free, thirty-day workspace retention and an ingestion cap; neither is a total-cost cap |
| Cosmos backup | Local periodic backups; configured retention eight hours, potentially extending physical retention of expired/deleted content |

Worker outages/backlogs can delay physical purge. Monitor them and verify real service retention after deployment; TTL/read filtering alone does not prove complete physical deletion. No backup, encrypted, derived, queue, or operational copy authorizes production content.

For a repeatable test reset, quiesce dev interactions and use `scripts\test-data\Reset-Fixtures.ps1`. It affects only six allowlisted conversations, increments their command epoch, restores fixture access, and retains operation evidence. It does not delete arbitrary containers/resources.

For teardown, first confirm the exact resource group and ownership/tags, stop traffic/jobs, and obtain deletion approval. Remove only that dedicated group, then separately remove the tagged Entra apps/users and GitHub federation/environment bindings after inspecting the private inventory. Do not delete unrelated resources or force-purge Key Vault; purge protection can retain keys after resource removal.

## Remaining live-readiness work

The tenant licence query returned no subscribed Microsoft 365 SKUs. Arrange Teams-enabled licences and dedicated test users through an approved administrator; an Azure subscription is not a Teams licence.

Live mode additionally needs approved Graph permissions/resource scope, real entitlement/history/DLP authority, rich callback and subscription/repair implementation, credential/notification-key rotation, and real browser/integration evidence. See ADR 0008; this is not a configuration toggle that can safely be enabled immediately after licensing.
