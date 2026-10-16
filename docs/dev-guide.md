# Dev setup and deployment

## Current status and boundaries

This is a simulator-first dev application, not a completed Teams integration. The application is deployed in Sweden Central at https://teamsrefdev-unc3jasrpkgbo-web-htdnfyf3dbbjbca2.b02.azurefd.net. Public readiness and desktop/mobile browser checks pass. User-specific sign-in consent is now provisioned; authenticated conversation/send journeys await a fresh Entra sign-in and any remaining first-use setup. No remote GitHub workflow has run.

The active deployment target is subscription `67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c`, tenant `f1640304-f816-412c-9e6f-4b6b818cf492`, and **Sweden Central** (`swedencentral`), explicitly selected by the user on 2026-10-16. The directory session now has Global Administrator; four dedicated test users and their application assignments are provisioned, and the web/callback managed-identity bindings are configured. No Graph message consent or licence assignment was performed.

Earlier Germany West Central what-if attempts failed for Redis Balanced B0/B3/B5 capacity. Sweden Central passed actual preflight and deployment. The working environment is `teamsrefdev-unc3jasrpkgbo-env-ready`, on `container-apps-ready`. An incomplete first environment was replaced after the required network feature was registered; data services and identities were preserved. Consult the validation report for the exact image, successful seed job and remaining authenticated-test boundary.

The earlier subscription was re-enabled and partially provisioned on 2026-10-15, including a successful ACR container build, but deployment failed on regional capacity. Those resources and their private identity inventory remain separate and were not deleted or reused for the new tenant; existing billable services may continue charging.

The current topology is one Sweden Central environment with Front Door Premium/WAF, private application origins/data services, and authenticated public ACR. Front Door/WAF are global resources; their private-origin connection uses Sweden Central. The deployment targets `rg-teams-reference-dev-swc`, leaving the earlier empty West Europe group and previous-subscription resources untouched. No regional recovery, load testing, or automatic regional fallback is included. See [ADR 0008](adr/0008-simulator-first-single-region-development.md).

## Hosted simulator behavior

The fake backend is the `Simulator` implementation of `IGraphTransport` inside the API process, not a separate anonymous demo site. It replaces Microsoft Graph message operations only. In Azure, Entra authentication, BFF sessions, Cosmos, Redis, Service Bus, Key Vault, Web PubSub and private-network enforcement remain real dependencies.

1. The user signs in through Entra; the API validates their fixture identity, session, conversation membership and visible history.
2. A send receives an operation ID and an expiring command window. The API journals and claims it, acquires the shared Redis send budget, then calls the simulator rather than Graph.
3. The simulator creates an exact synthetic receipt in Cosmos. Confirmation and the local projection/revision update follow the same business logic as the real transport boundary. The UI labels this as simulator confirmation, never Teams delivery.
4. A worker publishes body-free change hints through Web PubSub; clients fetch messages again through the authorized API. Synthetic external messages/edits/deletes instead enter Service Bus first, then the worker applies the projection and publishes hints.
5. Fixture-admin controls can reject, delay or throttle the next simulated send, or lose its confirmation after committing. A status check resolves the same receipt; it never resends automatically.

Seed jobs populate six allowlisted synthetic conversations in Cosmos. Browser drafts remain in memory, and no Teams resource or actual Teams message is read or written. Simulator mode does not remove the need for functioning Azure services or Entra users; local-memory tests cannot prove this hosted path. Live Teams mode stays disabled.

The earlier subscription's deployment retry encountered a subscription restriction on Cosmos allocation in West Europe and unavailable Redis B0/B1 capacity. Its attempted fallback kept compute/network in West Europe, placed only synthetic Cosmos data in North Europe over Private Link, and selected Redis B3 in West Europe (published instance rate EUR 0.0678/hour). This was not regional recovery or a completed deployment. Keep settings explicit per environment rather than carrying the old fallback into a new subscription. Key Vault public access remains disabled, with the trusted Azure-services exception enabled for ARM key provisioning; ordinary runtime access still uses private networking and RBAC.

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

Verify current Sweden Central rates, quotas, and subscription policies; the figures above are a historical planning snapshot, not a Sweden invoice estimate. Run actual what-if using approved identity IDs before applying. A compiled template is not an Azure what-if or proof that a SKU is deployable.

- [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices)
- [Front Door pricing](https://azure.microsoft.com/pricing/details/frontdoor/)
- [Service Bus pricing](https://azure.microsoft.com/pricing/details/service-bus/)

## Entra bootstrap

For the current identity/trust contract, safe command modes, and same-tenant/new-tenant rebuild procedures, use [Entra configuration and environment rebuild](entra-configuration.md). Its live desired-state profile and permission reconciliation are planned work, not additional capabilities of the current scripts.

Use the explicitly approved tenant/subscription, not whatever happens to be the CLI default. Keep the generated inventory outside the public repository.

```powershell
$tenant = '<approved-tenant-guid>'
$subscription = '<approved-subscription-guid>'
$inventory = Join-Path $env:USERPROFILE ".teams-reference-dev\$tenant\identity.json"
az login --tenant $tenant
.\scripts\entra\Bootstrap-Dev.ps1 -TenantId $tenant -SubscriptionId $subscription -InventoryPath $inventory
# After directory-change approval:
.\scripts\entra\Bootstrap-Dev.ps1 -TenantId $tenant -SubscriptionId $subscription -InventoryPath $inventory -CreateTestUsers -Apply
```

The bootstrap creates/reuses tagged `web`, `sync`, `events`, and `deploy` registrations/service principals, and optionally four dedicated unlicensed users. It grants no Graph message permissions or licences. User creation/role assignments may require an authorized directory administrator; stop on permission errors rather than substituting another tenant or identity.

Use `-SubscriptionId` on both directory scripts when multiple tenants are signed in; this selects the matching account/token without changing the shared Azure CLI default. Bootstrap verifies an existing inventory's tenant before changes and checkpoints successful creation with `bootstrapComplete=false`. Infrastructure-only deployment can proceed from valid web/callback application IDs, but application/fixture workloads still require four distinct approved actor IDs. Incomplete identities never enable an unauthenticated application or seed traffic.

The web service principal requires assignment, with only the fixture users assigned by bootstrap. The runtime independently checks the fixture identity allowlist before issuing a BFF session; an arbitrary user from the same tenant is not admitted to this synthetic-only app.

Initial user passwords are stored as Windows-DPAPI-protected credential XML beside the private inventory, require change at first sign-in, and never enter source or ordinary logs. An authorized administrator can recover a credential locally with `Import-Clixml`; do not print it into shared transcripts. MFA/Conditional Access and initial password-change interactions remain human steps.

### Sign-in consent is a separate prerequisite

Creating users and assigning them to the web application does not grant consent to its sign-in scopes. The deployed web app requests only `openid profile offline_access`, dynamically during authorization. Its registration has no static Graph message permissions.

The user-reported **"Need admin approval"** screen is a consent gate, not another MFA enrollment screen. Inspection found no existing permission grant for the web service principal. An administrator must approve the required sign-in scopes; do not disable MFA, add Teams permissions, or make the app unassigned/public to work around it.

`Grant-DevSignInConsent.ps1` prepares a least-privilege plan for the four already-assigned fixture users. It uses user-specific `Principal` grants rather than tenant-wide `AllPrincipals`, validates the tagged application and standard scopes, preserves assignment restrictions, and refuses unexpected broader existing grants.

```powershell
# Read-only validation; no consent is granted:
.\scripts\entra\Grant-DevSignInConsent.ps1 -SubscriptionId $subscription -InventoryPath $inventory

# Only after explicit approval, using an authorized administrator/client:
.\scripts\entra\Grant-DevSignInConsent.ps1 -SubscriptionId $subscription -InventoryPath $inventory -Apply
```

The user explicitly approved and executed the consent command on 2026-10-16. A subsequent read verified exactly four `Principal` grants with `openid profile offline_access`, no tenant-wide grant, and assignment restrictions still enabled. Restart Alex's sign-in to verify the actual authorization flow; no completed application session is implied by the grant records alone. No Azure redeployment is necessary.

Future application of the command requires a supported administrator role and an appropriately authorized Graph token. It does not elevate permissions or request broader client consent automatically.

See [Microsoft's delegated-grant contract](https://learn.microsoft.com/en-us/graph/api/oauth2permissiongrant-post?view=graph-rest-1.0). This sign-in consent does not approve the future live Teams permission matrix.

| Identity | Initial purpose |
| --- | --- |
| web | Single-tenant confidential OIDC BFF; managed-identity federation after platform creation |
| sync | Reserved separate background Graph identity; no message permissions granted in simulator delivery |
| events | Explicit callback audience and `PubSub.Callback` application role for the designated PubSub identity |
| deploy | GitHub OIDC subject `repo:<owner>/<repo>:environment:dev`; no directory administration |

## Platform and first workload deployment

Install Bicep 0.48.1 or newer. The implementation host's older 0.28 compiler lacked current resource schemas; validation used a session-local 0.48.1 binary without replacing global tools.

Register the required resource providers in a new subscription. Container Apps also required the following network feature in this subscription; verify its state is `Registered` before provisioning an environment. The platform script now checks this prerequisite without changing it during what-if.

```powershell
az feature register --subscription $subscription --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress
az provider register --subscription $subscription --namespace Microsoft.Network
az feature show --subscription $subscription --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress --query properties.state
az provider register --subscription $subscription --namespace Microsoft.AlertsManagement
```

```powershell
.\scripts\New-DevPlatform.ps1 -SubscriptionId $subscription -InventoryPath $inventory -Location swedencentral
# Review the actual what-if, cost and RBAC changes; then, after approval:
.\scripts\New-DevPlatform.ps1 -SubscriptionId $subscription -InventoryPath $inventory -Location swedencentral -Apply
```

The defaults now use Sweden Central for compute, data, endpoints and deployment metadata. `CosmosLocation` defaults to `Location`. Redis Balanced B0 successfully provisioned in Sweden; use that setting for this environment rather than carrying over a failed Germany SKU.

Initial platform deployment needs resource-group creation and scoped role-assignment rights. It creates identities and data services before application traffic. It does not deploy workload images. Routine releases pass `provisionRbac=false` and do not need Azure role-assignment or Entra administration rights.

Create a private ARM workload parameter file from the completed inventory: `webClientId`, `eventsClientId`, `fixtureActorIds` in manifest persona order, and `location`/`cosmosLocation` set to `swedencentral`. The `infra\parameters\dev.bicepparam` environment-variable template is another option.

Build the image with Docker, push to the authenticated ACR, and obtain its repository manifest digest. ACR Tasks can perform the build when a local Docker engine is unavailable; review its cost/permissions first. Do not pass credentials in build arguments or copy private inventories into build context.

```powershell
.\scripts\Deploy-Dev.ps1 -SubscriptionId $subscription -ResourceGroup rg-teams-reference-dev-swc `
  -ParametersPath '<private-parameters.json>' -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>'
# Initial rollout, after approval; admission remains disabled:
.\scripts\Deploy-Dev.ps1 -SubscriptionId $subscription -ResourceGroup rg-teams-reference-dev-swc `
  -ParametersPath '<private-parameters.json>' -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>' -Apply
```

Obtain the Front Door origin, API identity principal, and PubSub principal from the deployment outputs. After the corresponding directory approval:

```powershell
.\scripts\entra\Complete-Dev.ps1 -SubscriptionId $subscription -InventoryPath $inventory -PublicOrigin '<https-origin>' `
  -ApiIdentityPrincipalId '<api-principal-guid>' -PubSubPrincipalId '<pubsub-principal-guid>'
```

Inspect and approve the Front Door-created private origin connection in the Container Apps environment. Do not disable public-network restrictions as a workaround. Complete DNS/private endpoint checks and verify the private service paths from the in-network jobs.

Rerun `Deploy-Dev.ps1` with `-Apply -EnableRealtimeAdmission`. This installs the authenticated connect handler before enabling connection negotiation, seeds/verifies the allowlisted fixture dataset, and checks Front Door readiness. Existing templates enforce only simulator mode.

The initial admission-disabled revision uses process liveness as its platform readiness probe so the deployment can finish and run the seed job. The final admission-enabled revision switches to `/health/ready`, which verifies fixtures and Redis; authorization and public-network restrictions are never relaxed during bootstrap.

After rollout, run `scripts\Test-DeployedApp.ps1` with the public origin, tenant ID, and web client ID. It checks the compiled frontend, real fixture/Redis readiness, anonymous API denial, disabled local login, and the correct Entra authorization redirect. It explicitly reports authenticated browser journeys as not tested; complete first-use password/MFA interactions and run those scenarios separately.

Initial credentials for this tenant are stored as DPAPI-protected `alex.credential.xml`, `sam.credential.xml`, `jordan.credential.xml`, and `taylor.credential.xml` beside the private tenant inventory. Use `Import-Clixml` locally to obtain the `PSCredential`; never copy credential output into source, CI logs or support artifacts. The observed first-login screen is **"Let's keep your account secure"**. Security enrollment/MFA and any initial password change must be completed by the user; they are not automated or disabled to unblock testing.

### Callback failures behind Front Door

The initial authorization redirect and token-code redemption must use the same external HTTPS URI. Rewriting only the browser redirect is insufficient: Microsoft.Identity.Web can otherwise reconstruct the internal HTTP origin during code redemption, producing `AADSTS500112`.

The app validates `App:PublicOrigin` and configures both the OIDC redirect and named MSAL confidential-client `RedirectUri` to `<public-origin>/auth/callback`. Keep the Entra Web redirect registration identical. Do not add the internal HTTP origin to Entra or accept arbitrary forwarded-host headers as a workaround.

After changing the app configuration/code, start a fresh sign-in from the application root. Do not refresh a previously failed callback or replay an authorization code. Public readiness and redirect-only smoke checks do not prove token redemption; the proxy regression test covers the actual token request, while the final user session still needs a fresh real login.

## GitHub Actions

CI builds/tests the app, runs Playwright scenarios, compiles Bicep, parses PowerShell, and uploads sanitized local-test evidence. It receives no deployment credentials.

Before enabling `Deploy dev`, create the GitHub `dev` environment with a required reviewer and main-only deployment policy. Grant the deployment principal resource-group Contributor and registry-scoped AcrPush only after approving bootstrap. Do not grant subscription Owner or directory administration to the workflow.

Set environment variables: `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_DEPLOY_CLIENT_ID`, `AZURE_REGISTRY_NAME`, `TEAMS_WEB_CLIENT_ID`, `TEAMS_EVENTS_CLIENT_ID`, `TEAMS_FIXTURE_ACTOR_IDS` (four-element JSON array), and `TEAMS_BOOTSTRAP_COMPLETE=true` only after final bindings/private-origin readiness.

Set `AZURE_LOCATION=swedencentral`, `AZURE_COSMOS_LOCATION=swedencentral`, and `AZURE_REDIS_SKU=Balanced_B0`. Defaults now match the user-selected Sweden deployment. Do not reuse the old subscription's settings or Entra client/user IDs.

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

The original tenant had no Microsoft 365 subscriptions. The newly selected tenant reports enabled Microsoft 365 E5 (no Teams) and Microsoft Teams Enterprise subscriptions. This confirms product availability, not available seats, per-user assignments, service provisioning, consent, or live-readiness approval. Dedicated test users still require an authorized administrator; no licences have been assigned by this deployment.

Live mode additionally needs approved Graph permissions/resource scope, real entitlement/history/DLP authority, rich callback and subscription/repair implementation, credential/notification-key rotation, and real browser/integration evidence. See ADR 0008; this is not a configuration toggle that can safely be enabled immediately after licensing.

The selected implementation direction is the final event-driven projection, not a direct-Graph-read MVP. Follow the [live integration plan](live-integration-plan.md) for dependency-ordered delivery. This direction does not change the synthetic-only boundary or approve business-content storage, broader consent, licences, or a rebuild.
