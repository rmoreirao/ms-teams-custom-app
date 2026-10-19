# Entra configuration and environment rebuild

| Field | Value |
| --- | --- |
| Status | Simulator rebuild runbook, deployed three-user delegated GraphPilot configuration, and locally verified production identity reconciliation; no production app-only message grants applied |
| Reviewed | 2026-10-19 against repository scripts, Bicep, and local correctness evidence |
| Implementation plan | [Live Teams integration plan](live-integration-plan.md), particularly W0/W1/W7/W8 |
| Current deployment | See the [dev guide](dev-guide.md) for the selected tenant/subscription and Sweden Central deployment; do not treat historical IDs as reusable defaults |

## 1. Keep desired configuration separate from discovered IDs

An identity inventory is not a rebuild specification. Reuse logical names, ownership tags, role definitions, approved permission values, trust rules, and deployment inputs. Resolve actual directory/Azure IDs and reapprove access for the target environment.

| Layer | Current implementation | Planned final contract |
| --- | --- | --- |
| Desired state | `infra\identity\profile.schema.json` and `profile.example.json`; `Sync-Identity.ps1` validates exact tenant, purposes, grants, users/groups, callback principals, and federation | Complete the deployment-specific endpoint/installation matrix and approval evidence; RSC installation is not automated |
| Resolved state | Legacy `identity.json` schema version 1 is preserved; the reconciler writes a separate schema-v2 inventory with IDs, grants, federation, profile hash and reconciliation checkpoints | Live subscription/key/repair state resides in Cosmos; back up and recover it separately from directory inventory |
| Credentials | Initial fixture passwords are DPAPI-protected XML outside the repository; the web client uses managed-identity federation | Protected references and explicit rotation/recovery procedures; separate client-authentication and notification-decryption keys |
| Evidence | Current deployment/validation notes and manual CLI inspection | Read-only live drift/verification output and readiness evidence, stored privately without tokens or message bodies |

The reconciler has local safety coverage and was applied/verified against Entra for the approved **basic GraphPilot** profile. That profile sets `basicGraphValidation=true`, exactly three assigned users, and user-specific `Principal` grants for `openid profile offline_access Chat.Read ChatMessage.Send Channel.ReadBasic.All ChannelMessage.Read.All ChannelMessage.Send`. The five additional delegated scopes are not production background permissions. See the [basic-mode rebuild/run/evidence guide](graph-pilot.md), including temporary resource-creation permissions and their verified cleanup.

There is still no one-command production rebuild: platform preparation, policy authority, licences, consent approvals, trust rotation/removal, and production integration/recovery evidence remain explicit steps. The simulator bootstrap scripts do not consume the new profile. Do not run the sign-in-only consent helper against the broader approved pilot grants or remove those grants to make it pass.

## 2. Current registrations and trust

`Bootstrap-Dev.ps1` uses the ownership tag `teams-reference:<Prefix>` and names `<Prefix>-<purpose>`. The default directory prefix is `teams-reference-dev`; it is distinct from the Bicep resource prefix `teamsrefdev`.

| Purpose | Configured today | Final responsibility |
| --- | --- | --- |
| `web` | Single-tenant confidential Web app, implicit flows disabled, explicit fixture-user assignment required; callback/federation finalized separately | Cookie/BFF sign-in and ordinary user-delegated sends; no app-only user impersonation |
| `sync` | Tagged application/service principal reserved without Graph message roles or workload federation | Approved unattended subscription, hydration, reconciliation, and selected control calls |
| `events` | Audience `api://<events-client-id>` and application role `PubSub.Callback` | Authenticate the designated Web PubSub upstream principal; no Graph message permission needed |
| `deploy` | GitHub environment federation for `repo:<owner>/<repo>:environment:dev` | Scoped Azure release identity, not a directory/consent administrator |

The callback role definition ID is `277c2a98-926e-469c-8bd4-eb51680d6377`. Its assignment connects the PubSub managed identity to the `events` resource service principal. Keep the role definition stable in desired state; recreate/reconcile the assignment against actual principals.

In the private inventory, `applications.<purpose>.objectId` is the application-registration object ID, `.clientId` is its application/client ID (`appId`), and `.principalId` is the enterprise application/service-principal object ID. They are not interchangeable.

### Federation and callback values

| Binding | Required value in the current global-cloud implementation |
| --- | --- |
| Web redirect | `<public-origin>/auth/callback`, registered as a Web redirect; the same URI is used for authorization and code redemption |
| Web logout URL | `<public-origin>/`, as configured by `Complete-Dev.ps1` |
| Web FIC name | `api-managed-identity` |
| Web FIC issuer | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| Web FIC subject | API **user-assigned managed identity principal/object ID**, not its client ID or Azure resource ID |
| Web FIC audience | `api://AzureADTokenExchange` |
| Web runtime credential | `SignedAssertionFromManagedIdentity`, selecting the API managed identity by its **client ID** |
| GitHub FIC | Name `github-dev`, issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, exact environment subject above |
| PubSub upstream | `<public-origin>/callbacks/pubsub`, audience `api://<events-client-id>`, assigned role `PubSub.Callback`, and the exact PubSub principal allowlisted by the API |

Federated client authentication proves the backend's identity; it does not grant Graph permissions. Azure RBAC, Graph delegated consent, Graph application roles, app admission, and callback authentication remain separate.

The managed-identity federation method uses a user-assigned identity in the same tenant as the app registration. Do not extend these global-cloud URLs/audiences to a sovereign cloud without a separately verified configuration.

## 3. Existing commands and their actual safety modes

| Command | Preview/read-only behavior | Mutation and limits |
| --- | --- | --- |
| `scripts\entra\Bootstrap-Dev.ps1` | Without `-Apply`, prints provisioning intent; it is not a live directory diff | `-Apply` plus `ShouldProcess` creates/reuses tagged apps/SPs and GitHub trust. `-CreateTestUsers` creates/reuses three tagged unlicensed users (Alex, Sam, Jordan) and their web assignments. It grants no message permissions or licences. |
| `scripts\entra\Grant-DevSignInConsent.ps1` | Without `-Apply`, reads and validates the selected tenant, application, assignments, and existing grants | `-Apply` plus `ShouldProcess` grants only `openid profile offline_access` to the three assigned users using `Principal` grants. Refuses broader/unexpected grants, including Teams scopes. |
| `scripts\entra\Complete-Dev.ps1` | Use `-WhatIf` to preview intent; this does not inspect live drift | Has **no `-Apply` switch**. Without `-WhatIf`, confirmation permits redirect/federation/role changes. Refuses an existing web FIC with a different subject; it does not rotate trust or remove old PubSub assignments. |
| `scripts\New-DevPlatform.ps1` | Without `-Apply`, performs Azure subscription what-if and prerequisite checks | `-Apply` plus `ShouldProcess` provisions the dev platform/RBAC, not Graph consent or workload images |
| `scripts\Deploy-Dev.ps1` | Without `-Apply`, validates workload inputs and runs resource-group what-if | Defaults to simulator workloads/fixture seed. Explicit GraphPilot mode configures the API and skips seeding, but still uses full-platform Bicep; prefer the API-only helper for routine pilot releases. `-EnableRealtimeAdmission` is a later simulator rollout step, not production Graph enablement. |
| `scripts\entra\New-GraphPilotResources.ps1` | Without `-Apply`, validates private inventory and reports the dedicated resource plan | Approved apply creates only the tagged test resources, using checkpointed creation and a temporary setup application removed afterward; never substitutes business conversations. |
| `scripts\entra\Prepare-GraphPilot.ps1` | Reads actual deployment/federation and scope metadata; writes prepared private files | Creates the three-user profile, workload parameters and validation metadata outside the repository; changes no permissions. |
| `scripts\Deploy-GraphPilot.ps1` | Default plan and `-WhatIf` do not mutate or write patch snapshots | Approved apply updates only the existing API template, preserving platform/sizing and requiring the requested revision plus public readiness; never seeds fixtures. |
| `scripts\entra\Sync-Identity.ps1` | Defaults to `-Mode Plan`: live read-only diff. `-Mode Verify` fails on drift. `-Mode Export` writes a private schema-v2 inventory, subject to `ShouldProcess`. | `-Mode Apply` requires confirmation; user/group admission and consent changes additionally require `-ApprovePermissionChanges`. Changed named trust requires `-ApproveTrustRotation` and a pre-mutation live-state recheck. Broader/unlisted access is refused, not removed. |
| `scripts\Deploy-Live.ps1` | Defaults to actual Azure what-if with workload, RBAC, synchronization and admission switches all off; validates private platform and Strong Cosmos consistency | `-Apply` requires confirmation. Separate opt-ins select workload deployment, RBAC, synchronization and admission. Exports actual role IDs privately; never seeds fixtures, grants Entra consent, or changes licences. |

Use an authorized operator and Graph client/token for each directory action. Azure Contributor does not grant Entra administration, and an administrator role alone does not add missing permissions to the calling client. Do not grant directory administration or consent-management roles to ordinary deployment/runtime identities.

`Complete-Dev.ps1` performs individual operations, not an atomic directory transaction. Review all proposed values before allowing it to run; a later refusal does not imply earlier changes were rolled back.

### Distinguish the two bootstrap flags

Private inventory `bootstrapComplete=true` means the bootstrap script completed its directory provisioning sequence. It does **not** establish sign-in consent, licences, final managed-identity bindings, private-origin readiness, or live Teams readiness.

GitHub variable `TEAMS_BOOTSTRAP_COMPLETE=true` is a separate operator-controlled release gate, set only after the dev guide's final bindings and network readiness steps. Do not copy the inventory flag directly into the workflow gate. The final live implementation needs independently verified readiness predicates, not another overloaded boolean.

## 4. Rebuild the currently implemented simulator environment

This sequence documents available commands, not permission to execute them. Directory changes, grants, licence assignment, infrastructure cost, and access removal need their own approval. Preserve existing evidence and inspect ownership before rebuilding; no deletion is required by this runbook.

### Step 1: select the target and private inventory

```powershell
$tenant = '<approved-tenant-guid>'
$subscription = '<approved-subscription-guid>'
$prefix = 'teams-reference-dev'
$repository = 'rmoreirao/ms-teams-custom-app'
$inventory = Join-Path $env:USERPROFILE ".teams-reference-dev\$tenant\identity.json"
az login --tenant $tenant
```

For another logical environment in the same tenant, select a distinct approved prefix and private inventory path. Keep the prefix stable when reusing existing registrations. Always pass `-SubscriptionId` so directory scripts select the matching account/token without changing the shared CLI default.

### Step 2: restore or reconstruct directory objects

```powershell
.\scripts\entra\Bootstrap-Dev.ps1 -TenantId $tenant -SubscriptionId $subscription `
  -InventoryPath $inventory -Prefix $prefix -Repository $repository
```

After approving directory changes, use the same command with `-Apply`; include `-CreateTestUsers` only for the approved fixture accounts. A lost inventory can be reconstructed by resolving the existing tagged apps and users with the same tenant/prefix; duplicate names or ownership mismatches must stop the process.

Existing tagged users are reused, not given new passwords. Missing DPAPI credential files cannot be reconstructed from Graph or regenerated by re-running bootstrap. Their recovery requires a separately authorized credential process. DPAPI-protected files are not portable recovery material for another operator/machine; keep any approved recovery procedure outside source control. First-use password changes, MFA, and Conditional Access remain human steps.

### Step 3: establish sign-in consent independently

```powershell
.\scripts\entra\Grant-DevSignInConsent.ps1 -SubscriptionId $subscription -InventoryPath $inventory
```

Review its read-only result; use `-Apply` only after approving the exact three-user sign-in grants. No Teams scopes are included. If the application already has broader live grants, this simulator-specific helper intentionally refuses; do not remove those grants to make it pass.

### Step 4: provision platform and initial workloads

Follow [platform and first workload deployment](dev-guide.md#platform-and-first-workload-deployment), including provider/feature registration, actual what-if, quota/cost approval, image digest, private parameters, and an admission-disabled first deployment.

Derive parameters from the selected inventory: `webClientId`, `eventsClientId`, and three fixture IDs in **alex, sam, jordan** order. Keep `location`, `cosmosLocation`, and `redisSku` explicit; current dev defaults are Sweden Central and Balanced B0. The environment-variable Bicep template uses separate `TEAMS_ACTOR_*` values; the GitHub workflow instead uses `TEAMS_FIXTURE_ACTOR_IDS` JSON. Existing simulator data requires the explicit retired-actor migration in the [test guide](testing.md#fixture-manifest-and-personas) before three-user rollout; seeding never silently resets it.

Read the actual resource-group release outputs:

```powershell
az deployment group show --subscription $subscription --resource-group '<approved-resource-group>' `
  --name teams-reference-release --query properties.outputs --output json
```

Save the resolved binding metadata privately. Required current outputs include `publicOrigin`, `apiIdentityClientId`, `apiIdentityPrincipalId`, and `pubsubPrincipalId`. The subscription-level platform wrapper currently exports only `registryLoginServer`; do not assume it exports the full identity map.

### Step 5: bind the actual origin and principals

```powershell
.\scripts\entra\Complete-Dev.ps1 -SubscriptionId $subscription -InventoryPath $inventory `
  -PublicOrigin '<actual-https-origin>' -ApiIdentityPrincipalId '<actual-api-principal-guid>' `
  -PubSubPrincipalId '<actual-pubsub-principal-guid>' -WhatIf
```

For a first binding, review and approve the values, then omit `-WhatIf` and respond to its confirmation. For changed principals, stop and follow the explicit rotation procedure in section 7; the current command does not reconcile replacement trust.

The Entra redirect and `App:PublicOrigin` must match the actual Front Door/custom origin. Do not register an internal HTTP callback or trust arbitrary forwarded hosts to bypass a mismatch.

### Step 6: reopen only the implemented dev path

Approve the private-origin connection, establish private service access, configure the GitHub environment/OIDC variables from actual outputs, then follow the admission-enabled release sequence in the dev guide. Require a fresh real Entra sign-in; do not replay an old authorization code or callback URL.

These steps restore simulator functionality only. An Azure rebuild does not assign Teams licences, grant live permissions, activate the reserved sync client, or implement Graph callbacks.

## 5. Live permission and identity profile

The final environment profile must record the exact endpoint, API version, auth mode, permission value, consent scope, resource/installation boundary, purpose, and mutable-source reference. Resolve permission IDs against the target tenant's resource service principal; do not copy tenant-local principal IDs from another environment.

| Responsibility | Planned authorization | Constraints |
| --- | --- | --- |
| BFF sign-in | `openid profile offline_access`, approved app assignments and tenant/Conditional Access policy | Preserve assignment restrictions. Cookie sign-in is not Graph message consent. |
| Ordinary chat/channel sends | Delegated `ChatMessage.Send` and `ChannelMessage.Send` on `web`, only for enabled capabilities | Declare and grant explicitly. Normal projection reads do not require broad delegated read scopes. No migration role or app-only send substitution. |
| Tenant-wide chat subscription/hydration, if explicitly approved | Application `Chat.Read.All` on `sync` | This is a broad candidate, not an approved default. `/chats/getAllMessages` subscription scope and permitted retained resources must be reconciled with G0. |
| Tenant-wide channel subscription/hydration, if explicitly approved | Application `ChannelMessage.Read.All` on `sync` | Same scope/privacy gate; verify all selected subscription and hydration endpoints. |
| Narrower resource/installation scope | Exact supported RSC/installed-scope permission and Teams app installation for the chosen endpoints | Read support does not establish rich-notification support. Verify API-version differences and quota fit before selecting this alternative. |
| Discovery and entitlement/control | Only the permissions required by the selected team/channel/chat/member and lifecycle authority endpoints | Message-read permission does not establish history/DLP or identity-lifecycle authority. Do not grant a speculative bundle. |
| Web PubSub upstream | `PubSub.Callback` assignment to the current PubSub identity on `events` | Separate callback audience/protocol from Graph notifications |
| Runtime Azure access | Role-specific Cosmos, Redis, Service Bus, PubSub, Key Vault, and registry access | ARM roles and Graph consent are not interchangeable; the current combined worker's access is not the final least-privilege split |

Use distinct environment profiles for synthetic, staging/live-test, and production resources. The selected architecture favors approved aggregate scope instead of subscriptions per browser/user; the final scope choice must fit the tenant's shared subscription quota without quietly ingesting unrelated resources.

Broad subscription approval must cover the actual receipt and processing boundary, not only the messages ultimately shown to users. Filter unapproved resource metadata before durable acceptance where supported; filtering after a broad encrypted payload has been stored does not undo that content copy.

### Declarations are not grants

Three independently verified objects are needed where applicable:

| Configuration | Meaning |
| --- | --- |
| Application `requiredResourceAccess` | Declares the intended delegated scopes/application roles; does not itself grant consent |
| Delegated `oauth2PermissionGrant` | Actual approved scopes on behalf of specified users (`Principal`) or all users (`AllPrincipals`, separately approved) |
| Service-principal `appRoleAssignment` | Actual application-role grant to the background client, or a role assignment on the callback resource |

In a delegated grant, `clientId` is the **client service-principal object ID**, not the application's client ID. `resourceId` is the resource service-principal object ID. For application roles, resolve the target resource's enabled role definition and assign it to the intended client principal.

Discover the Microsoft Graph resource service principal using its stable global application ID `00000003-0000-0000-c000-000000000000`; its service-principal object ID is tenant-local. Store permission values and types in desired state, then validate their resolved IDs and enabled status.

Do not infer production `AllPrincipals` consent from the three-user simulator helper. The final planner must compare declared permissions, actual grants, user/group admission, and installation scope, and refuse unexpected broader access. Access removal is a separate approved operation, not implicit drift cleanup.

### Background credentials and rich-notification prerequisites

Prefer the supported user-assigned-managed-identity federation pattern already used by `web` for the `sync` confidential client, subject to the chosen deployment's support. Bind only the approved control/hydration workload identity; the notification receiver and delivery worker do not need general Graph credentials.

Configure the subscribing `sync` service principal for rich notifications separately from the user-facing `web` principal. Microsoft's recommended rich-notification setting is `appRoleAssignmentRequired=false` on the subscribing app's service principal. If policy requires it to remain true, implement the documented Microsoft Graph Change Tracking assignment prerequisite. Otherwise rich notifications can contain a null validation token. Do not disable assignment requirements on the browser-facing app to solve this.

Client-authentication trust and notification encryption are separate. The subscription includes the approved public encryption certificate and certificate ID; the private decryption key stays protected for the projector. Record key versions, overlap, rotation, allowed crypto operations, queue/backup lifetime, and restore access. A new certificate cannot decrypt old queued payloads.

Track each subscription's actual accepted expiry, callbacks, lifecycle URL, resource scope, certificate ID, owner, and repair state privately. Teams subscriptions requested beyond one hour require a lifecycle URL. A subscription ID in an old inventory is not evidence that it still exists or is authorized.

### Implemented reconciler and remaining automation

Copy `infra\identity\profile.example.json` to a private environment-specific profile, replace the intentionally invalid zero tenant/subscription IDs, and populate only reviewed grants, assignments, callback principals, HTTPS redirects and exact federation subjects. Do not edit the public example into a credential inventory.

```powershell
$profile = '<private-identity-profile.json>'
$resolved = Join-Path $env:USERPROFILE ".teams-reference-live\$tenant\identity.resolved.json"
.\scripts\entra\Sync-Identity.ps1 -ProfilePath $profile -InventoryPath $resolved -Mode Plan
# Only after approval of the displayed changes and exact profile:
.\scripts\entra\Sync-Identity.ps1 -ProfilePath $profile -InventoryPath $resolved -Mode Apply -ApprovePermissionChanges
.\scripts\entra\Sync-Identity.ps1 -ProfilePath $profile -InventoryPath $resolved -Mode Verify
```

`Apply` performs a complete read-only preflight before directory writes, checkpoints each successful operation and then verifies/exports the result. Offline checks cover interrupted consent followed by resume, no-op reapply, lost-inventory rediscovery, broad-grant/trust-drift refusal, and no file/directory writes under `-WhatIf`. Schema-v1 bootstrap inventory is not overwritten or silently migrated.

The implemented sync profile requires `assignmentRequired=false` for rich notifications. The alternative Change Tracking-role configuration is not provisioned. `assignedGroups` supports approved web-app group admission; per-user delegated consent still requires that user to be directly assigned, or a separately approved `AllPrincipals` grant. Group-based assignment licensing and effective membership remain deployment checks.

Changed named federation is visible in Plan and may be applied only with `-ApproveTrustRotation`. The reconciler rereads its exact name/issuer/subject/audience immediately before PATCH and refuses a concurrent change. Use an approved quiescence/overlap plan: replacing trust does not instantly revoke already-issued tokens. Unlisted federation removal, redirect replacement, callback-audience replacement and permission removals are not implicit cleanup. User/licence creation, RSC/Teams app installation and policy-service permission provisioning remain separate operations.

For live workload staging and the profile-to-runtime mapping, use the [live runtime and rollout guide](live-runtime.md). Keep profile/grant declarations separate from actual accepted subscriptions and coverage state.

For each privileged operation, document the supported administrator role and calling-client permission requirements. Routine CI/CD uses its environment federation and scoped Azure roles; it must not gain tenant consent or user/licence administration.

## 6. Required deployment inputs and runtime mapping

| Input or output | Current consumer | Rebuild rule |
| --- | --- | --- |
| Tenant/subscription | Explicit script flags, `AzureAd:TenantId`, `App:TenantId`, workflow `AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` | Resolve/verify together; do not change the shared CLI default or mix inventories |
| Web application client ID | `webClientId`, `TEAMS_WEB_CLIENT_ID`, `AzureAd:ClientId` | Reuse only the verified same-tenant registration |
| Events application client ID | `eventsClientId`, `TEAMS_EVENTS_CLIENT_ID`, `Callbacks:Audience`/`ClientId` | Audience is `api://<client-id>`; it is not a service-principal ID |
| API managed-identity client ID | `AzureClientId`, `AZURE_CLIENT_ID`, `AzureAd:ClientCredentials:0:ManagedIdentityClientId` | Select the actual attached user-assigned identity |
| API managed-identity principal ID | Web FIC subject and Azure role assignments | Rebind explicitly when the identity is recreated |
| PubSub principal ID | Callback role assignment and `Callbacks:PrincipalId` | Reconcile both sides; changing runtime settings alone does not revoke old assignments |
| Public origin | Entra Web redirect/logout, `App:PublicOrigin`, PubSub upstream URL | Derive from the actual edge output/custom domain; keep authorization and redemption identical |
| Fixture user object IDs | `App:FixtureActorIds`, Bicep/fixture manifests, `TEAMS_FIXTURE_ACTOR_IDS` | Preserve persona order; never reuse another tenant's users or seed them into live business data |
| Deploy client/repository/environment | `AZURE_DEPLOY_CLIENT_ID`, GitHub FIC subject, protected environment | Current subject is fixed to `dev`; final automation must parameterize and validate environment-specific trust |
| Background client/role identities, keys, resource scopes | Not wired into current simulator workloads | Add explicit outputs/profile-to-runtime mapping in W1/W7; do not imply existing settings enable live mode |

The final infrastructure must output resource, client, and principal IDs for every role separately. Keep sensitive/key material out of outputs; use protected references. The final live workflow must not invoke the simulator seed/reset path.

## 7. Rebuild and rotation scenarios

| Scenario | Reuse | Recreate/reconcile before admission |
| --- | --- | --- |
| Routine image release | Existing verified registrations, principals, grants, identities, data, and subscriptions | Image/schema compatibility and runtime readiness; no blanket bootstrap, re-consent, or fixture seeding for live mode |
| Azure-only rebuild in the same tenant | Owned Entra apps and eligible existing grants/users | Actual managed-identity IDs, RBAC, web/sync federation, PubSub assignment/allowlist, new origins, protected settings, and data/network readiness |
| Lost private identity inventory | Tagged objects that can be unambiguously rediscovered | Reconstruct/verify IDs, grants, trust, assignments, and checkpoints; do not reset existing users or claim lost credentials are recoverable |
| New tenant or new logical environment | Versioned logical profile, schemas, role definitions, approved code | Tenant-local apps/SPs/users, verified domain, licences/service plans, consent, installations, resource mappings, keys, federation, and workflow inputs |
| Notification key rotation or restored queue | Approved key references and retained versions needed for outstanding encrypted work | New subscriptions/key IDs, controlled overlap, old-payload decryption, ownership and coverage; never drop backlog silently |
| Data/journal/control-state loss | Authoritative Teams content and surviving approved evidence | Restore and evaluate each state family; fence sends and stale authorization, reconcile coverage, and invalidate old command authority as required |

For a changed web or sync managed identity, the dev finalization script still refuses replacement trust. Use the live profile/reconciler's explicit named `-ApproveTrustRotation` path after reviewing the old/new trust, or a separately approved overlap/removal procedure. Preflight the principals and affected access, quiesce affected dispatch, verify supported token use, then explicitly remove superseded trust/access if the overlap approach was selected. Do not delete the FIC to bypass a refusal and continue blindly.

PubSub replacement requires an assignment to the new principal and the matching runtime allowlist, followed by approved removal of superseded assignments. Redirect changes require the same coordinated treatment of the Entra registration, backend origin, and routing.

For a new tenant, check available licence seats, approved usage location/service plans, per-user assignment, and actual Teams provisioning. Bootstrap creates unlicensed users and does not purchase or assign licences. MFA/security enrollment and consent remain explicit readiness steps.

Preserve operation evidence and writer ownership through a rebuild. Rehydrating Teams history cannot prove that an uncertain POST did not commit. Do not resend commands from a lost journal or treat a restored browser session as permission to bypass fresh authority.

## 8. Acceptance and source references

The final rebuild path must demonstrate empty-environment creation, same-state no-op reapply, partial-failure resume, drift refusal, lost-inventory rediscovery, changed-principal rotation, and post-rebuild identity/subscription operation. Verify real sign-in/token exchange and approved Graph behavior separately from object existence.

Keep that evidence distinct from the current simulator's public smoke and directory grants. No existing script flag certifies a production deployment or bypasses the [G0-G7 gates](technical-design.md#113-acceptance-gates).

- [Configure an application to trust a managed identity](https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity)
- [Delegated permission grant resource and identifier semantics](https://learn.microsoft.com/en-us/graph/api/resources/oauth2permissiongrant?view=graph-rest-1.0)
- [Grant an application role to a service principal](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0)
- [Send a chat message: delegated permissions](https://learn.microsoft.com/en-us/graph/api/chat-post-messages?view=graph-rest-1.0)
- [Send channel messages and replies](https://learn.microsoft.com/en-us/graph/api/chatmessage-post?view=graph-rest-1.0)
- [Teams message subscription permissions, scope, and lifecycle requirements](https://learn.microsoft.com/en-us/graph/teams-changenotifications-chatmessage)
- [Rich-notification application configuration, validation, and encryption](https://learn.microsoft.com/en-us/graph/change-notifications-with-resource-data)

Permission support, subscription limits, cloud capabilities, roles, and licence requirements are mutable. Recheck the selected endpoint/version and target tenant before implementing or applying the final profile.
