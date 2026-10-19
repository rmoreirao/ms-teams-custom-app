# Basic Microsoft Teams validation

| Field | Result |
| --- | --- |
| Decision | On 2026-10-19 the user selected basic functionality on existing infrastructure, deferring the stronger architecture |
| Hosted app | [Open the Teams-backed app](https://teamsrefdev-unc3jasrpkgbo-web-htdnfyf3dbbjbca2.b02.azurefd.net) |
| Backend mode | Explicit `App:GraphMode=GraphPilot`; not a simulator and not the production `Live` projection mode |
| Validated identities | Alex, Sam and Jordan, each with a fresh password/TOTP sign-in |
| Real Teams results | Each account read the dedicated chat/channel, sent a chat message, posted to the channel and replied to its thread |
| Creation evidence | All nine Graph message IDs were confirmed and read back with the expected author and exact synthetic content |
| Infrastructure | Existing API, Front Door, Session-consistency Cosmos, Redis and Key Vault; no new Azure resources or resource sizes |

## What this mode does

The existing BFF calls Microsoft Graph with the signed-in user's delegated token. Only the three assigned test identities and one explicitly tagged synthetic group chat/channel pair are admitted. The dedicated resources have the `Teams Reference Validation` prefix and were created for this validation; no existing business conversation was selected.

Reads go directly to Graph. Each request uses a bounded working set of the latest 50 messages or thread replies, exposed through the existing 20-message UI pages and seven-day/start-of-test cutoff. This is not an unlimited history archive. Changes refresh every 30 seconds; this mode does not claim notification-driven delivery or production-scale capacity.

The existing operation journal prevents duplicate application submissions. A message is confirmed only after a real Graph creation response. Definitive pre-dispatch failure is rejected; possibly committed POSTs remain unknown and are never automatically resent. Server persistence stores body-free operation evidence and resource metadata, not message-body projections.

`GraphPilotStore` is scoped to an HTTP request. Graph-derived message bodies exist in that request's memory and are stripped from every persistence operation. Delegated Graph calls enforce the user's source permissions/history; this test mode does not claim full Purview/DLP policy parity.

Simulator controls are absent in this mode. There is no fallback between `GraphPilot`, `Simulator` and `Live`.

## Approved identity and resource setup

The user approved these five delegated permissions for the existing web application, using three user-specific `Principal` grants, not tenant-wide user consent:

- `Chat.Read`
- `ChatMessage.Send`
- `Channel.ReadBasic.All`
- `ChannelMessage.Read.All`
- `ChannelMessage.Send`

The previous `openid profile offline_access` grants remain. Web-app assignment stays restricted to the three users. No runtime app-only message-reading/sending or migration permission was granted.

All three users were already enabled and had two assigned licence products with Teams service plans enabled. The CLI administrator was unlicensed and could not create a team through delegated Teams provisioning. With separate user approval, a one-hour temporary setup application used only `Team.Create`, `Channel.ReadBasic.All` and `Chat.Create` to create/resolve the dedicated resources. Its private key stayed in process memory, and the application was removed afterward; active temporary application and service-principal counts were both verified zero. The team was created with one licensed owner, then the other two users were added to its backing group.

No tenant role assignments were changed. Global Admin is not required for ordinary app reads/sends and is not a substitute for delegated consent or source membership.

## Repeatable private configuration

Passwords and TOTP seeds remain only in the ignored root `.env`, using `TEST_ALEX_*`, `TEST_SAM_*` and `TEST_JORDAN_*`. They are consumed by the validation browser, never the backend. `.env*`, browser state and reports are excluded from image build context.

The private tenant directory contains:

| Artifact | Purpose |
| --- | --- |
| `graph-pilot-scope.json` | Exact created team/channel/chat IDs, approved actors, tag and start cutoff; creation checkpoint prevents blind duplicate POSTs |
| `graph-pilot\identity.profile.json` | Reviewed three-user delegated permissions and existing federation/redirect settings |
| `graph-pilot\identity.resolved.json` | Actual verified consent/identity snapshot |
| `graph-pilot\workloads.parameters.json` | Existing-deployment bindings plus explicit GraphPilot resources |
| `graph-pilot\validation.settings.json` | Nonsecret target/identity/resource metadata used to pin browser validation |
| `graph-pilot\image.json` | Immutable registry image reference |
| `graph-pilot\validation-results\` | Per-account Graph IDs/read-back results and post-authentication app screenshots, without passwords/tokens/cookies |

Rebuild/prepare from an approved tenant's private inventory:

```powershell
.\scripts\entra\New-GraphPilotResources.ps1 -SubscriptionId $subscription `
  -InventoryPath $inventory -CheckpointPath $scope
# Use -Apply only after approving the specific resource/temporary-permission setup.
.\scripts\entra\Prepare-GraphPilot.ps1 -SubscriptionId $subscription `
  -InventoryPath $inventory -ScopePath $scope -OutputDirectory $pilotDirectory
.\scripts\entra\Sync-Identity.ps1 -ProfilePath "$pilotDirectory\identity.profile.json" `
  -InventoryPath "$pilotDirectory\identity.resolved.json" -Mode Plan
# After approving the exact per-user grants, apply and verify with the same reconciler.
```

For routine basic-mode releases, use `Deploy-GraphPilot.ps1`, which patches **only the existing API**. It snapshots the previous template privately, preserves sizes/replicas/identities/network, removes unsupported read-only ARM fields, and requires the requested image's latest revision to be ready before checking public readiness. It does not run a fixture seed or full platform deployment.

```powershell
.\scripts\Deploy-GraphPilot.ps1 -SubscriptionId $subscription -ResourceGroup $group `
  -AppName $api -ParametersPath "$pilotDirectory\workloads.parameters.json" `
  -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>'
# Review, then add -Apply for the approved API-only update.
```

The actual validation deployment used this same API-template patch strategy, producing revision `teamsrefdev-unc3jasrpkgbo-api--0000004`. The standard Bicep also accepts the explicit GraphPilot mode for a later approved full rebuild; do not run a full platform deployment merely to update this API.

## MFA and optional passkey registration

All three accounts passed password and existing authenticator TOTP. Entra then offered an optional passkey-registration campaign. Both passkey choices are new credentials, not TOTP seeds.

The user explicitly approved selecting **Not now** only when offered on the exact **Let's keep your account secure** screen after password/TOTP. The runner requires `ENTRA_AUTH_PROFILE=graph-pilot`, `ENTRA_DEFER_OPTIONAL_REGISTRATION=true`, a visible matching heading/control, no visible rejected-code state and no remaining OTP prompt. It defers once per flow. Missing deferral, mandatory setup, passkey-only sign-in or credential rejection still stops automation.

No MFA, Conditional Access, authentication method or tenant security policy was disabled or changed. New passkeys were not enrolled, existing TOTP seeds were not replaced, and persistent browser sign-in was declined.

## Run real Teams validation

```powershell
$env:PILOT_VALIDATION_SETTINGS = "$pilotDirectory\validation.settings.json"
$env:ENTRA_AUTH_PROFILE = 'graph-pilot'
$env:ENTRA_TEST_BROWSER = 'chromium'
# Set only with approval to defer the offered optional registration campaign:
$env:ENTRA_DEFER_OPTIONAL_REGISTRATION = 'true'
Push-Location src\Web
npm run check:entra
npm run test:entra:unit
node scripts\check-entra-signin.mjs alex --teams
node scripts\check-entra-signin.mjs sam --teams
node scripts\check-entra-signin.mjs jordan --teams
Pop-Location
```

Without `--teams`, the checker remains authentication-only and blocks frontend business operations. With `--teams`, it verifies the private target metadata, expected user, real GraphPilot session, dedicated catalog, UI actions, Graph creation IDs, exact read-back authors/bodies and final authenticated app screenshots. It does not retry an uncertain send.

Final local checks passed: 92 backend tests, 6 React component tests, frontend build/type-check, 12 authentication-runner tests, PowerShell/identity/deployment guards, dev/live Bicep compilation and CI workflow lint. These support, but do not replace, the separate three-user actual Graph evidence above. The reusable API-only helper was checked against the current deployment in read-only plan mode; the hosted rollout used the equivalent targeted patch strategy, not a second deployment solely to exercise that helper.

## Boundary for later work

This basic validation does not establish the final event-driven architecture's capacity, rich-notification, Strong-consistency, organizational policy-authority or regional-recovery acceptance. Those remain in the [implementation plan](live-integration-plan.md) and [live runtime guide](live-runtime.md). The production `Live` mode's stricter gates were not relaxed to enable this test.
