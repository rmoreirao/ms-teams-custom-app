# Live runtime configuration and rollout

**Status: the combined Session/availability-first release is deployed to the five Development roles; final migration acceptance/cleanup and independent production activation remain gated.** Real storage/crypto, signed notification-to-browser delivery and delegated chat/reply confirmation have historical evidence; see [current validation](dev-validation.md). The new release's signed-in read/hydration and unattended/recovery acceptance remain distinct from that earlier evidence. No production business-content processing was activated. The earlier [GraphPilot basic mode](graph-pilot.md) passed real delegated Teams journeys but is no longer the current hosted transport.

**Current source is Live-only.** Simulator and legacy GraphPilot modes, persona sign-in, fixture seeding and pilot polling have been removed. Development is the only environment. On 2026-10-21 its approved cloud migration deployed five isolated workloads with application-authenticated Control and Graph-backed policy. The three test users are not background credentials. `DelegatedSynthetic` is retained only as guarded pre-acceptance migration compatibility. Use [five-role image-only Development releases](dev-guide.md#github-actions) for routine updates; preserve the reviewed existing platform.

The 2026-10-22 combined rollout added only the approved refresh queue and its
exact Api Sender/Control Receiver grants, paired the new image with explicit
Session settings and completed/cleaned the isolated real-Cosmos check.
Operational alert configuration/routing is explicitly deferred to future work;
logs and diagnostics remain. See [deployment evidence and pending acceptance](dev-validation.md#combined-service-rollout-2026-10-22).

Follow the [implementation status](live-integration-plan.md#execution-status) and [Entra rebuild runbook](entra-configuration.md). G0, a trustworthy policy authority, licensed/approved resources, actual Graph endpoint support/budgets, capacity, and recovery evidence still gate release.

## Runtime and storage boundaries

| Role | Entry points and responsibilities | Required data access |
| --- | --- | --- |
| `Api` | Cookie/OIDC BFF, guarded projected reads, durable cold-view requests, paged catalog, delegated sends, operation status, authenticated PubSub admission | Projection read/write for commands; authority read; read-refresh queue send; Redis session/cache/permits; session-protection key; PubSub |
| `Receiver` | `/callbacks/graph/notifications` and `/callbacks/graph/lifecycle`; validation challenge, signed rich-token/client-state checks and encrypted durable acceptance | Control-state read; notification queue send; no message/authority writes, private decryption key or Graph credentials |
| `Projector` | Lock-renewing Service Bus consumption, authenticated decryption, conditional merge/tombstones, repair scheduling and bounded dead-letter retention | Projection/control read/write, queue send/receive, notification-key decrypt; no authority writes or Graph credentials |
| `Delivery` | Cosmos change-feed outbox dispatch, broker-driven leased fan-out pages, coalesced body-free hints and current-recipient authorization | Projection/checkpoint and change-feed lease read/write; authority read; delivery queue send/receive; PubSub |
| `Control` | Leased subscription ownership/renewal, bounded read-refresh consumption, bootstrap/hydration, verification/repair and authoritative policy refresh | Projection/control/authority read/write; read-refresh queue receive only; shared Graph permits; sync federation; approved policy-service access; PubSub revocation |

Use independent instances of the same image, or the explicitly bounded compact topology below. `All`, fixture roles, local credentials, memory persistence and retired mode selections are rejected. Fake dependencies exist only in automated tests and are not a runtime fallback.

`infra\live.bicep` creates five distinct user-assigned identities, three state containers (`live-state`, `live-authority`, `live-control`), a separate `/id`-partitioned `live-leases` container, `graph-notifications`, `graph-delivery` and `graph-read-refresh` queues, private Front Door origins/WAF and role-specific workloads/access. It targets an **existing dedicated private platform**; it does not create or resize its Cosmos account, VNet/private endpoints, Service Bus namespace, Redis, PubSub or Key Vault. The dev serverless/session-consistency platform is not a production sizing template.

The live Cosmos client requires explicit `readConsistency: "Session"` and uses native Session SDK reads for ordinary content. Missing settings and the old `Strong`/`SessionPrimary` profile names are rejected without aliases. Startup and deployment validate one readable/writable region and a Session or Strong account policy; they never change the account or require a consistency upgrade. See [ADR 0012](adr/0012-session-content-reads-and-revision-convergence.md).

The template retains the canonical `readCurrentV1` procedure on the three data containers and, under the explicit RBAC provisioning switch, a custom reader role with `executeStoredProcedure` but no item-write actions. The API's authority access remains unable to create/replace/delete items. Because execution permissions are container-scoped, do not add mutating procedures to reader-accessible containers. A critical current read never falls back to an ordinary read if the procedure, permissions or response evidence is unavailable.

`IPartitionDocuments.ReadAsync` is an ordinary SDK point read; `ReadCurrentAsync` explicitly requests the procedure. Content display and delta hydration use ordinary reads/queries. Grants, revocations, exact-pair authority, parent eligibility, operation/epoch evidence, merge/tombstone decisions, delivery and subscription/repair controls remain current. Conversation metadata remains current because it also gates security epochs and source coverage; its SDK partition barrier benefits later content queries without a procedure call for each message. Independent clients do not share tokens automatically. RU/latency improvement requires measurement; this is not multi-region recovery or capacity certification.

Messages are separate documents, not aggregates or per-recipient copies. Projection, change-journal and outbox updates share a conversation partition; authority and control are separate containers. Data-plane roles prevent the API/projector/delivery identities from rewriting authoritative access. Catalog updates across partitions are recoverable and never replace a fresh source-membership check.

## Availability-first reads and hydration

[ADR 0013](adr/0013-availability-first-reads-and-bounded-hydration.md) separates
content availability from source health. Healthy coverage is no longer required
to display stored, authorized history. There is no source-content age deadline
that hides it during repair; a missed edit/deletion may remain visible until
reconciliation or seven-day content expiry. Current authority, revocation,
history, mapping, parent eligibility and epochs remain strict.

Redis is a revision-keyed message cache. A miss/failure falls back to Cosmos.
Available or genuinely known-empty views return `200`; partial pages keep a
continuation and can request optional refresh. An empty uninitialized query
requires durable work and a bounded authorized reread, not a false empty result.
After a five-second total server budget, an accepted cold request can return:

```json
{ "state": "pending", "retryAfterMs": 1000, "access": null }
```

That is HTTP `202` with `Retry-After: 1`. A verified eligible view can include
`access`; an unknown parent cannot supply fresh retention permission. The browser
automatically retries cold/older-page loading within 120 seconds, retains
previously loaded authorized history/drafts during operational failures, renews
access proactively and clears content when authority expires or fails. A queue
submission failure is `read_refresh_unavailable`, not accepted pending work.
Cosmos/security-store failure never authorizes a Graph-only response.

`graph-read-refresh` contains only approved tenant/conversation/epoch/parent/page
context and request time. Control derives Graph paths from the registry and uses
its existing credentials/shared permits. Context leases coalesce replicas; original
redelivery waits for lease/backoff instead of being falsely acknowledged. Each
job has a 45-second execution budget, at most three 50-item source pages plus an
optional parent GET, and at most two concurrent handlers. Progress persists after
each page. Chat paging is creation-descending; channels do not assume that order.
Equal-timestamp older-page hydration uses the same ascending message-ID tie-break
as the projected query, so it continues past the requested cursor instead of
returning pending after a page entirely before that cursor.
Hydration uses existing version/tombstone merge, journal/outbox and body-free hints.
Source page absence does not imply deletion.

Matching-epoch body-free `read-view` initialization/continuation markers have no
TTL; they are not evidence of current source health. Real legacy completed
coverage is preserved with its original verification timestamp before invalidation.
Marker progress does not increment material revision or clear global repair gaps.
Regular verification and reconciliation remain necessary.

### Release prerequisites

The source adds a queue in the existing namespace, not new Graph permissions,
identities, sizing or account/network settings. Provisioning and grants require
separate approval:

- `graph-read-refresh`: five-minute TTL, expiry dead-lettering, one-minute lock,
  five deliveries and one-minute duplicate detection.
- API identity: **Azure Service Bus Data Sender at this exact queue**.
- Control identity: **Azure Service Bus Data Receiver at this exact queue**;
  retries renew locks/wait/abandon and require no sender grant.

`infra\service-dev-access.bicep` provisions this dependency only under its explicit
access switch. `infra\service-dev.bicep` remains workloads-only.
`Deploy-ServiceDev.ps1` checks queue properties and exact queue-scoped grants
read-only before image mutation; it does not create/repair them or normalize
private inventory. Compact templates grant both operations to their API principal
because it also hosts Control; compact release scripts do not have the five-role
ARM dependency preflight. Synchronization-enabled API/Control startup validates
data-plane access in either topology without submitting/consuming work.

Build a new coordinated image, including the matching frontend for the new read
`202` contract, and use a fresh browser load for acceptance. The earlier prepared
Session-only image does not contain it. If combining with the Session migration,
approve the queue/grants as an additional dependency and retain matching previous
image/settings pairs. Offline evidence does not approve or establish deployment.

### Support and recovery

1. Separate authority/storage failure from source health. Verify current grants,
   epoch/mapping and parent eligibility; never extend leases to keep content visible.
2. Inspect body-free refresh request/coalescing/failure/progress counters,
   `teamsref_read_refresh_age_ms`, all three queue backlogs/DLQs and
   `teamsref_repair_stalled`. Use work/checkpoint age, not the age of the last chat
   message. Normal Graph-budget/rate deferral is distinct from blocked/authentication
   failure; stalled-repair observation uses a longer threshold for ordinary deferral.
3. Check queue existence/scoped access, the Control receiver and existing Graph
   identity/permits. Inspect repair/subscription progress with the existing guarded
   diagnostics. Available stale history does not make an unhealthy source pass
   `ProjectionCheck`.
4. Refresh poison, expired, fenced and fifth-failure work is explicitly dead-lettered.
   There is **no automatic refresh-DLQ replay**. Inspect its body-free cause, correct
   the approved dependency, then use a fresh authorized request or separately
   reviewed recovery. Do not replay arbitrary source URLs, reset projection/authority
   state or resend messages. An expired refresh continuation has no separate automatic
   restart machine; broader reconciliation remains available.
5. Enable/verify the monitoring module and its approved action group before claiming
   routed support. Compiled alerts or an empty action-group list prove no notification
   destination. Retained UI availability and source-freshness acceptance remain
   separate observations.

## Configuration

### Service-authenticated Development

The normal service path selects `authorityMode=Policy`, `entitlementProvider=Graph`, one hosted role per process and five distinct `roleIdentityClientIds`. It uses the existing sync registration through federation from Control's managed identity. The API keeps its separate Web registration and current-user delegated sends; no interactive principal or password is used for background access. Graph-profile workloads select managed identity explicitly rather than falling back to a developer's CLI identity.

`GraphPolicy` contains the reviewed policy approval reference, Web service-principal object ID, admitted user IDs, allowed direct app-role IDs, existing resource history floors and explicit send-enabled resources. Draft/pending references and legacy synthetic actor/owner/history settings are rejected. This provider models a bounded membership/direct-admission/account-state/history policy; approval must establish that the selected test resources do not require additional unimplemented visibility/DLP/moderation policy. It is not a claim of general enterprise Teams policy equivalence. Normal Graph delegated sends remain the final source enforcement for each user write.

The provider independently reads the Web enterprise application's assignment requirement/enabled state, direct user assignments, admitted users' enabled/member state, chat membership and history boundaries, exact one-to-one pairs, and supported standard-channel/team conditions. Removed/disabled/unassigned users receive inactive grants; unsupported conditions are denied. Missing, mismatched or unproven source evidence fails explicitly. Private/shared/archived/moderated or cross-tenant cases outside this contract are not silently enabled. Group members require an established source history boundary; one-to-one history is bounded by the verified chat creation and configured cutoff.

Shared identity evidence and resource decisions are cached only within their original evidence window. Returned leases are capped by the oldest source snapshot; reading cached evidence cannot renew it. Reusing unchanged proof avoids redundant authority/catalog writes while still repairing a previously interrupted catalog update. The API continues to recheck revocation, fresh grants, history and source-pair evidence.

Application Graph access is restricted in code to approved resource contexts and configured policy fact paths. The seven sync application read roles below were explicitly approved and applied in the confirmed dedicated test tenant. They are actual tenant-wide grants; the application allowlist does not reduce their Entra scope. They are never assigned to Web:

| Approved sync permission | Required operation |
| --- | --- |
| `Chat.Read.All` | Existing chat metadata, membership, messages and per-chat subscriptions |
| `ChannelMessage.Read.All` | Existing channel posts/replies and per-channel subscriptions |
| `Channel.ReadBasic.All` | Supported channel metadata/type |
| `Team.ReadBasic.All` | Approved team metadata/archive state |
| `TeamMember.Read.All` | Standard-channel parent-team membership |
| `User.Read.All` | Only admitted users' identity, enabled state and member/guest classification |
| `Application.Read.All` | Only the configured Web service principal and its direct admission assignments |

The background wrapper cannot post messages. It permits only approved encrypted subscription definitions and operations on subscription IDs registered for that resource. Canonical pagination/scope/budget matching and the P1 repair/backoff protections are preserved. Capacity is awaited only before dispatch under a bounded deadline; Graph POSTs are never automatically retried after dispatch.

Normal service `/api/session` reports `backgroundAuthentication=application`, `entitlementProvider=Graph`, and `scopedValidation=false`. Test users still authenticate for interactive use and delegated sends; `ENTRA_AUTH_PROFILE=service` pins this contract without requesting background read or chat-creation scopes. The Control-only `Inspect` diagnostic reports metadata without using user contexts; Api cannot invoke service diagnostics or acquire the sync application's identity.

The Api-only `ProjectionCheck` diagnostic is a separate, read-only data-plane acceptance check. It uses Api's own managed identity to read every approved user's current catalog and bounded snapshots, verify history limits, deny the nonmember of each direct pair, and open only the verified existing contacts. Its proof contains counts, not message bodies or session tokens. It sends no Graph messages and does not mint cookies or claim browser/OIDC/delegated-send acceptance. Both service diagnostics run only as explicitly selected process operations, not public API endpoints.

The Api-only `SessionCheck` storage probe is a separate opt-in write/cleanup operation, not part of read-only smoke. It uses two independent Gateway clients under the same role-bound managed identity and accepts only a named `session-primary-validation-*` container. It verifies the actual account topology, bounded Session content convergence, current revocation and epoch fencing, then deletes only its tracked synthetic documents. No Teams messages are sent. The isolated container's existing procedure and explicit scoped data permission are prerequisites; any temporary grant and tracked cleanup require approval and must not remain as excess Api access afterward.

This service path is deployed. Five sync-owned rich subscriptions were confirmed with exact resource, callback, lifecycle, encryption and client-state bindings. The old combined Control writer was fenced before activation, the existing epoch/resource histories were preserved, and excess compact-role Cosmos/ARM privileges were retired. No source data or uncertain-operation journals were reset. Fresh coverage and the expected memberships for all five resources were established before admission was enabled. Admitted public smoke passed. Subscription renewal, a fresh authenticated journey and the observation beyond the former eight-hour session bound remain explicit acceptance checks, not consequences of ARM success or offline tests. Keep the guarded legacy source until those checks are accepted; its removal is a post-cutover step.

### Bounded existing-platform Azure validation

`authorityMode=DelegatedSynthetic` is a separately validated, three-test-user profile, not a production policy-service substitute. It reuses the existing dedicated chat/channel and their three approved one-to-one pairs. Fresh Graph metadata/membership, exact member sets, per-user source reads and explicit history cutoffs establish short authorization leases. The API hosts `Api`, `Receiver` and `Control`; the existing worker hosts `Projector` and `Delivery`. This compact topology is rejected outside the explicit synthetic profile.

Background Graph access uses the existing delegated consent and protected server-side token cache. Retained original user principals are bound to tenant, actor and session, and cannot outlive their original cookie expiry or eight hours. They contain neither passwords nor raw Graph tokens. A fresh sign-in is required when those contexts expire; the profile does not invent an unattended production credential. Live token/session caches have a distinct namespace from GraphPilot.

New configuration for this compatibility topology also requires `Session` on the existing single-region serverless account. `infra\synthetic-live.bicep` supplies separate projection, authority, control and lease containers, read-only procedures, scoped data/queue permissions, encrypted notifications and the versioned notification key. The existing account, service tiers, two Container Apps and their CPU/memory/replica caps remain unchanged. Compact API/control hosting necessarily shares authority-write access; the separate production identity boundaries above still apply to `Policy` deployments.

Keep resolved settings, Azure snapshots, operation results and post-authentication evidence outside the repository. The operational sequence is:

1. Generate private settings from current Live metadata with `New-SyntheticLiveSettings.ps1`, including explicit existing app names and `OperationJobName`; inspect actors, resources, owners, history cutoffs and identity/consent bindings. Initial setup supplies approved Live configuration directly, never a retired pilot snapshot; see [the rebuild sequence](dev-guide.md#platform-and-first-workload-deployment). Reconcile `graphNotificationPublisher=true` through the identity profile: only Microsoft Graph Change Tracking receives Default Access, while the three user assignments, existing consent and federation remain unchanged.
2. Apply the scoped infrastructure and reconcile the existing edge with `infra\waf-managed-rules.json`, preserving its other settings. Both normal deployment templates use that same policy. It excludes only JSON values `claims.iss`, `claims.aud` and `headers.Origin` from enabled RFI rule `931130`, because real Web PubSub CloudEvents contain those standard HTTPS URL fields. These body fields do not authorize the callback. WAF prevention, other fields/rules and the required callback JWT role/principal checks remain enabled; do not add an unconditional callback-path Allow rule. Allow for global WAF propagation and inspect the edge's configured diagnostic sink, which may differ from the app workspace; query matched field names, not token values.
3. Use `PrepareNotification` through `Invoke-SyntheticLiveOperation.ps1` to obtain the public certificate and verify remote Key Vault encryption/decryption without exporting a private key. Pin its exact key version in private settings.
4. For an approved configuration change use `Deploy-SyntheticLive.ps1`; ordinary image-only releases use `Deploy-Dev.ps1`. Establish three fresh user sessions, verify only the three explicitly configured existing pair mappings with `EnsurePairs`, and inspect coverage/current grants with `Inspect`. Pair verification creates no chat and preserves each resource's history cutoff.
5. Run `SessionCheck` and `RealtimeCheck` in the existing manual job. The former uses independent Cosmos clients and tracked synthetic cleanup; the latter verifies a real private-network WebSocket and its approved actor. Jobs disable automatic retries and restore the original job definition afterward.
6. Only after approval of the exact real sends, run `src\Web\scripts\validate-azure-event-path.mjs --verify-azure` with private `AZURE_LIVE_SETTINGS`, schema-v2 `LIVE_VALIDATION_SETTINGS`, and `ENTRA_AUTH_PROFILE=live-delegated`. Keep original users/resource IDs/scopes; the profile describes existing consent, not another runtime. See [helper migration](testing.md#real-service-authentication-and-teams-checks).

`Probe` journals exactly one external Graph chat post and one channel post before dispatch, and deliberately never calls the projection engine. `VerifyProbe` requires authenticated notification provenance, matching projected source versions and the durable published revision. The browser first waits for authorized initial snapshots, then checks exact change-feed IDs, normal delegated chat/reply confirmation, no duplicate sends, a measured 31-second catalog-poll-free window and no business browser storage. Independent sends respect the unchanged shared actor budget. A possibly dispatched or unresolved probe is never replayed automatically.

The operation runner saves the exact Azure execution identity, disables job retries and restores the original manual job definition. If the live log stream is unavailable after a successful execution, it retrieves only that execution's operation-proof records from the existing workspace, with bounded ingestion retries. This recovery was exercised against actual Azure with log-stream failure injection; it never repeats a source POST.

Subscription definitions preserve URI-safe Teams identifier punctuation (`:` and `@`) while stored scope comparisons use the canonical escaped path. When migrating subscriptions created with the older encoded definition, use fresh subscription-scope identities; do not retarget logical conversation resources or replay uncertain creates. Preserve existing subscription-scope IDs; changing a generator name is not a reason to rotate or retarget them.

Graph lifecycle envelopes observed in Azure can omit `tenantId`. They still require a current tenant-partitioned subscription lookup, configured resource and constant-time matching secret client state; a supplied tenant must match. Rich message notifications additionally require the matching tenant and signed publisher validation tokens. `Reauthorize` recovers only the five registered synthetic subscriptions and schedules coverage repair for any missed interval. The control client accepts the reauthorization endpoint's successful empty entity, including observed HTTP 200, without treating empty message/read responses as valid JSON.

An unhandled reauthorization signal whose original subscription is confirmed missing (`404`) is reconciled as removal: rotate its client state and checkpoint a replacement under the same approved scope. Other reauthorization failures remain pending rather than triggering a replacement. If a replacement response is lost, exact-correlation recovery resolves that original creation intent without another POST.

Control handles a lifecycle reauthorization challenge and renewal with one `PATCH /subscriptions/{id}` operation, including when renewal would otherwise not yet be due. It does not issue `/reauthorize` and then a renewal PATCH in the same maintenance pass: Graph warns that those operations within ten minutes can cause subscription state inconsistencies. The lifecycle signal is acknowledged only after confirmed renewal is checkpointed; a definitive `404` retains fresh-correlated replacement and uncertain-create recovery semantics.

Source membership leases do not establish production DLP equivalence, an Entra offboarding propagation bound, arbitrary resource onboarding, 200,000-user capacity or regional recovery. Those remain distinct acceptance work.

Coverage renewal starts just inside four minutes remaining on the existing five-minute lease. Subscription/authority refresh and repair run in independent loops, so a long Graph verification cannot block access-lease or subscription maintenance. Repair work runs concurrently across resources but serially within each resource, with at most twenty checkpointed hydration/verification steps shared across that resource's jobs per pass. A new root renewal waits for all pending thread repairs to finish, rather than repeatedly restarting the root before coverage can be committed. Every Graph attempt still uses the existing shared capacity permits; a deferral retains the last successful checkpoint. Completed verification can recover a failed coverage write on the next pass without waiting for a new message or rescanning the resource. That recovery keeps the original verification time and five-minute deadline, remains bounded by the subscription expiry, and cannot reuse expired or superseded proof or bypass pending reply repairs.

Graph pagination compares canonical resource identities across continuation validation, delegated scope enforcement and Graph budget keys. Raw/escaped Teams identifiers and supported OData key syntax identify the same approved resource; identifier case, encoded separators, HTTPS authority/version and exact thread boundaries remain enforced. The original continuation query is opaque and is never decoded or rebuilt.

Repair checkpoints remain backward compatible. Pending work is runnable, deferred with a bounded exponential backoff (two to sixty seconds plus up to one second of jitter), or blocked after an invalid continuation/scope. **Blocked work remains pending and prevents coverage restoration**; it is not silently completed or replaced by ordinary scheduling. Pending-job enumeration advances through bounded pages so blocked jobs do not permanently hide later runnable work. Failures retain their code, attempt count and next eligible time without advancing verification evidence. Successful progress resets retry eligibility while retaining the last failure and recovery metadata.

A superseded generation restarts from the approved resource/thread boundary before using its old cursor. A Graph-rejected continuation (`400`/`410`) can trigger one generation-bound restart from that boundary, protected by the checkpoint ETag; a repeated rejection is blocked. Invalid authorities or different resources are never followed. Recovery does not reset conversation data, extend old coverage evidence, change subscriptions/permissions, or replay message sends.

A continuous unavailable-subscription interval fences coverage once per controller instance, rather than advancing the repair generation on every maintenance pass. This preserves repair backoff while a delegated account requires sign-in. A verified available subscription closes that interval; a subsequent distinct gap fences the resource again. Failed invalidation writes remain retryable, and restarting a controller conservatively fences an ongoing gap again.

`Inspect` reports coverage expiry/generation, unresolved repair states, last progress, failure/backoff and recovery metadata without bodies or raw URLs/tokens. An unreadable remote subscription is reported separately from a confirmed missing subscription. Structured control warnings identify the logical resource and stage; duplicate stage/resource/code warnings are aggregated over one-minute intervals. Routine Microsoft Identity/MSAL and HTTP-client information logs are filtered to Warning while application warnings/errors remain available. The existing log-ingestion spending cap is unchanged; an exhausted workspace resumes ingestion at its scheduled reset, not merely because a quieter image is released.

Typed cold pending and retryable operational failures use bounded, conversation-specific safe-read retries (eight backoff delays with a fixed two-minute budget, then explicit retry remains available). Replica-lag delays have bounded jitter. Hints do not restart the budget. Operational/source-coverage failure alone does not clear a valid authorized view; authority expiry, revocation, epoch changes or unverifiable critical security reads do. Access renewal cannot fabricate fresh evidence. Retries cancel on view teardown and never introduce a periodic catalog poll or resend messages.

### Independent production roles

The template supplies `App:GraphMode=Live`, an independent `App:Role`, tenant, actual role identities, public callback origins, Azure endpoints and backend credential bindings. Supply the remaining nonsecret `LiveOptions` through `Live:Configuration` JSON or the corresponding `Live` configuration section. Unknown properties in the JSON form are rejected.

Start from `infra\parameters\live.parameters.example.json`, stored privately after replacing its intentionally invalid placeholders. It contains public encryption material and protected references, **never a PFX, private key, password, token or client secret**.

| Setting | Rule |
| --- | --- |
| `approvalReference` | Recorded deployment-specific data/resource approval; not a substitute for that approval |
| `readConsistency` | Required exact value `Session`; missing/legacy names rejected. One readable/writable region, Session/Strong account policy, and the procedure/permissions for critical current reads are required. No account setting is changed. |
| `writerEpoch` | Positive command-ownership epoch matching persisted conversation and policy evidence; changing it requires a fenced migration |
| `resources` | Unique URL-safe logical IDs (1-128 letters/digits/`_`/`.`/`-`), kind/title and actual chat/team/channel IDs. Reads are restricted to this allowlist and user authority. A logical ID cannot silently be retargeted to a different Graph conversation; use a new ID or an explicit fenced migration. |
| `directChats` | Optional approved existing one-to-one mappings: `resourceId`, `firstActorId`, `secondActorId`. The resource must be an approved chat, with unique resource/pair mappings, valid distinct GUIDs and at most 100 contacts per actor. Default empty; no directory grant or Graph chat creation is enabled. |
| `subscriptionScopes` | Explicit per-resource paths or approved `chats/getAllMessages` / `teams/getAllMessages` aggregate scopes; every resource must be covered |
| `notificationCertificateId` / `notificationCertificate` | Active identifier and base64 DER public RSA certificate, at least 2048 bits and unexpired |
| `notificationKeys` | Certificate-ID to **versioned** Key Vault RSA key URI; retain approved old versions through encrypted backlog/recovery overlap |
| `entitlementAuthority` / `entitlementScope` | Approved HTTPS policy-feed endpoint and token audience/scope for the control managed identity |
| `synchronizationEnabled` | Off by default; permits notification persistence/consumption, Graph subscriptions/reads and policy ingestion only after approval |
| `admissionEnabled` | Off by default; enables user API/dispatch admission only with synchronization enabled |
| `graphIntervalMilliseconds` / `dataIntervalMilliseconds` | Conservative shared background intervals; defaults 100/250 ms, with per-resource spacing and reserved control headroom. They do not certify tenant capacity. |
| `queueConcurrency` | Bounded projector concurrency, default 8; tune only after actual crypto/storage/queue evidence |

The template overrides callback URLs, sync client ID, control identity client ID, synchronization and admission from actual deployment inputs. Runtime clients disable redirects when carrying Graph/policy bearer tokens. Public browser responses contain no Graph access/refresh tokens.

## Required policy authority

The repository implements a client contract, **not a replacement for Teams/Entra/DLP authority**. Deployments must provide a supported policy integration and approve its semantics before enabling synchronization. Merely inventing grants from fixture membership or treating Graph message-read consent as user access is not valid.

The control managed identity requests `entitlementScope` and calls:

```text
GET <entitlementAuthority>/resources/<encoded-logical-resource-id>/entitlements
GET <entitlementAuthority>/resources/<encoded-logical-resource-id>/entitlements?cursor=<opaque>
```

Responses are bounded pages of at most 100 entries:

```json
{
  "tenantId": "<approved-tenant-guid>",
  "resourceId": "approved-chat",
  "entries": [
    {
      "tenantId": "<approved-tenant-guid>",
      "conversationId": "approved-chat",
      "actorId": "<user-object-id>",
      "name": "Approved member",
      "historyFrom": "2026-10-19T08:00:00Z",
      "verifiedAt": "2026-10-19T08:05:00Z",
      "freshUntil": "2026-10-19T08:06:00Z",
      "authorityVersion": "<opaque-source-evidence>",
      "active": true,
      "canSend": true,
      "epoch": 1
    }
  ],
  "cursor": null
}
```

For a resource configured in `directChats`, every policy page must additionally carry `directChat` evidence. This is a source-verified assertion of the actual Teams chat type and complete exact member set, not a restatement of the application's configuration:

```json
{
  "directChat": {
    "tenantId": "<approved-tenant-guid>",
    "resourceId": "approved-chat",
    "graphChatId": "<actual-approved-Teams-chat-id>",
    "chatType": "oneOnOne",
    "memberIds": ["<approved-first-user-guid>", "<approved-second-user-guid>"],
    "verifiedAt": "2026-10-19T08:05:00Z",
    "freshUntil": "2026-10-19T08:06:00Z",
    "authorityVersion": "<opaque-source-evidence>"
  }
}
```

The Control role validates the tenant/resource/Graph mapping, type and exact configured pair, then stores this evidence in the separate authority container. API discovery checks its fresh lease as well as both individual user grants. Two active grants do not prove there are no other chat members. Missing, expired, group-chat or mismatched-pair evidence fails closed; the API identity cannot create that authority evidence. Other resources omit `directChat`. Pair evidence has the same five-minute maximum lease, monotonic verification and no-renewal-from-identical-evidence rules; retargeting its Graph chat requires explicit migration.

Evidence leases cannot exceed five minutes, move backwards, or be extended using the same verification evidence. Missing/expired/conflicting authority fails closed. A removal must be represented by `active=false` with fresh evidence; omissions never extend old leases. The feed must revisit all active entries before lease expiry, page fairly, include policy/history/offboarding constraints, and prove its upstream propagation bound. If it cannot support the product's requirements at the selected scale, W0/W6 remain blocked.

The same authority version cannot be reused with a changed lease, policy or access decision at the same verification timestamp. Revocation closes existing PubSub connections; every subsequent read/send rechecks authority. Content-specific DLP/visibility rules that cannot be represented by this resource/history contract require an explicit contract extension, not a claim of equivalence.

## Session configuration migration

Source changes alone do not authorize Azure changes. The approved Development
migration is recorded in [deployment evidence](dev-validation.md#combined-service-rollout-2026-10-22);
other targets still need their own review. Settings using `Strong` or
`SessionPrimary` are incompatible with the new binary.
Routine image-only release scripts intentionally refuse them rather than rewriting
configuration or silently weakening a setting that claims Strong reads.

1. Capture each affected workload's exact image, ready revision, private settings
   and admission/synchronization state. Retain the previous image/configuration
   pair for rollback. Verify existing read-only procedures and role permissions;
   Session consistency alone requires no account upgrade or new grants. Combining
   it with availability-first recovery requires the separately approved queue and
   exact sender/receiver grants in [release prerequisites](#release-prerequisites).
2. Review a private configuration diff changing only `readConsistency` to
   `Session`, together with the intended image. Update the service-target inventory
   consistently. New generators require an explicitly migrated source inventory;
   they do not normalize legacy values silently.
   Rebuild/review the combined source image; an earlier Session-only image is not
   the availability-first release package.
3. Under separately approved admission/writer-fencing safeguards, use the existing
   configuration-capable deployment path (`infra\service-dev.bicep` for five service
   workloads), not an ordinary image-only release. Inspect what-if and preserve
   identities, resources, histories, writer epoch, subscriptions, queues, secrets,
   sizing and all unrelated settings. Coordinate roles and diagnostic jobs; do not
   leave an incompatible image/configuration combination active.
4. Before reopening admission, verify exact revisions, startup storage/queue
   validation, current authority, exact remote subscriptions and operational
   progress. Do not turn unhealthy global coverage into a blanket authorized-read
   gate. Verify strict source health, bounded snapshot/delta recovery and the
   signed-in read path separately before claiming migration acceptance; available
   history alone does not pass `ProjectionCheck` or reconciliation acceptance.
   Use the separate synthetic Cosmos check only with approval for its tracked
   writes/cleanup; no real Teams sends are required for that storage check.
5. Compare procedure calls for ordinary content, total RU/latency and catch-up
   retries with the baseline. Subsequent image-only releases require that all
   targets already satisfy the Session configuration contract.

Rollback restores the prior image **and its matching settings**, using the same
approval/fencing safeguards. It must not reset data, journal/outbox state, replay
an uncertain Graph POST, or remove the procedures/permissions needed by either
version. Keep historical validation-container names and previous release evidence.
Legacy identity/runtime/grant cleanup from the service-authentication migration is
separate work, not permission granted by this consistency cleanup.

## Staged deployment and rebuild

Directory mutations and billable provisioning require approval. These examples document the commands; do not execute them against a tenant merely to validate local code.

1. Resolve G0, target platform/network/retention requirements, licences/resources, policy authority, budgets, credentials and recovery constraints. Prepare the private live parameter/profile files.
2. Run `Deploy-Live.ps1` in default what-if mode against the selected **existing private platform** with explicit `readConsistency=Session`. Only after approval, use `-Apply -ProvisionRbac` to establish role identities, containers, read-only procedures, queue and edge without workload traffic.
3. Use its private `identityBindings`/`publicOrigin` outputs to complete the Entra profile: web FIC trusts the API **principal** ID; sync FIC trusts Control's **principal** ID; runtime selects identities by their **client** IDs. Set the web redirect/logout and PubSub callback assignment from actual outputs. Run `Sync-Identity.ps1` Plan/approved Apply/Verify.
4. Deploy the immutable image with `-DeployWorkloads`, leaving synchronization and admission off. Approve private-origin connectivity and validate DNS, role-specific data access, WAF/large callback handling and actual token exchange.
5. After approval, enable synchronization with `-DeployWorkloads -EnableSynchronization`; establish subscriptions, decrypt a real approved notification, finish coverage/bootstrap/repair, and verify the policy feed.
6. Pass the applicable G0-G7 evidence and independently approve admission. Use `-EnableAdmission` together with workload/synchronization switches only then. No fixture job is run at any stage.

```powershell
$arguments = @{
    SubscriptionId = '<approved-subscription-guid>'
    ResourceGroup = '<dedicated-live-resource-group>'
    ParametersPath = '<private-live.parameters.json>'
    OutputsPath = '<private-directory>\live.outputs.json'
}
.\scripts\Deploy-Live.ps1 @arguments
# After the respective approvals, add the specific opt-in switches and -Apply.
# For workloads also supply -Image '<registry>.azurecr.io/teams-reference@sha256:<digest>'.
```

The wrapper always sets workload/RBAC/synchronization/admission flags explicitly; parameter-file values cannot silently enable them. `-WhatIf` does not write resources or the private output file. Routine releases must repeat the intended enabled switches and **must not invoke dev seed/reset scripts**. Remote workflow execution, image push and directory mutation are separate actions.

`/health/live` is process health. `/health/ready` is bounded role/onboarding/dependency readiness, not a scan/certification of every resource or production SLO. Reads enforce current access/retention without treating coverage as a display deadline; release diagnostics separately require healthy synchronization. Readiness alone never approves user admission.

For same-tenant rebuilds, reuse verified owned registrations/grants, but reconcile actual principal IDs, trust, callbacks, runtime allowlists and RBAC. A changed named FIC requires the explicit `-ApproveTrustRotation` path and is rechecked immediately before mutation; unlisted FICs and permission removal still require separate review. For new tenants, recreate tenant-local objects/consent/installations/licensing and resource mappings. Preserve authority, operation, subscription, change-feed lease, delivery-progress and key-version evidence separately from messages; rehydration does not prove that an unknown POST failed.

## Correctness, retention and current limits

Recent reads use Redis revision-keyed pages or bounded Cosmos queries. A Redis miss/failure falls back only to Cosmos; a genuinely cold authorized Cosmos view can request bounded Control-owned Teams hydration as described above. The shared page is filtered by current history authority, including restrictions observed while loading it. Changes are journaled with projection updates; an expired/oversized catch-up gap requires a new authorized snapshot.

The catalog is fetched in bounded authorized pages, with a protected continuation header and explicit **Load more conversations** control. Current text filtering searches the loaded catalog pages; it is not a server-side full-history search index. New envelope and storage records carry schema version 1; unsupported versions fail explicitly.

### Browser synchronization and approved-contact discovery

The browser consumes `{conversationId, revision}` hints from Web PubSub, coalesces bursts for 100 ms and updates only the affected open conversation/thread. Known-conversation hints do not reload the catalog. A previously unknown conversation prompts an authorized catalog refresh. A jittered 4-6-minute reconciliation and foreground/reconnect reconciliation recover missed hints. There is no pilot polling transport, SSE application mode or automatic push-to-poll fallback.

`GET /api/conversations/{id}/changes?after=<revision>&minimumRevision=<target>` returns authorized upserts/tombstones, reply context, the current history cutoff and a renewed command window. Thread requests include `parent` so its current eligibility is checked even during lag. Successful reads and `503 projection_behind` errors carry `access: { historyFrom, validUntil, epoch }`; the deadline is capped by the original authority lease, not coverage. A reader behind `after` or the target is not a journal reset. Changed documents older than their journal entries also report lag rather than claim catch-up.

Genuine journal gaps/expiry, more than 100 entries or more than 256 KiB of upserts require an authorized snapshot. Hidden-parent replies are excluded. The browser never regresses its loaded revision, retains loaded pages on normal deltas, and rebuilds the loaded page count on a real reset. During lag or other operational failures, a still-valid same-epoch view can remain after current history/retention filtering; it expires locally at the verified deadline. Authorization/security failures clear it, and network failure does not renew access. Snapshot and older-page reads enforce the revision floor. Sending outcomes and unsent drafts are not replica progress.

Session metadata distinguishes `canStartDirectChats` from `canCreateDirectChats`. With approved `directChats` mappings, `Live` supports `/api/people` and `/api/chats/direct` using current policy evidence for both participants, source-verified exact-pair/type authority and the existing projected resource. It exposes names, not inferred email addresses or an organization-wide directory. Selection sends no message and performs no Graph call. A stale authority produces an explicit error; a revoked recipient disappears without expiring the caller's valid session. Unhealthy coverage does not block opening an approved chat; the subsequent message read distinguishes available, known-empty and cold pending content.

For example, after independently approving and verifying the actual Teams chat type/exact pair and its policy/subscription mapping:

```json
{
  "directChats": [
    {
      "resourceId": "approved-chat",
      "firstActorId": "<approved-first-user-guid>",
      "secondActorId": "<approved-second-user-guid>"
    }
  ]
}
```

This is safe reuse of an already approved chat, **not automatic creation/onboarding of a new production resource**. That still needs an approved discovery/creation boundary and authoritative registration/subscription/policy workflow; the API cannot grant itself authority or add arbitrary Graph IDs. The retired GraphPilot creation endpoint is not available.

Messages support paragraphs, emphasis, lists, quotes, code and HTTPS links through a maintained DOMPurify tag/attribute allowlist. Active markup, inline styles, embedded media and unsafe link schemes are removed; plain-text sends remain literal text. Attachments/embedded media have explicit notices, and a validated HTTPS link on `teams.microsoft.com` or `teams.cloud.microsoft` can open the source in Teams. Attachment transfer, arbitrary cards and full Teams feature parity are not implemented. Deleted records clear copied text/HTML, notices and source links. These additive projection fields remain within the same approved retention/copy inventory.

Loaded histories above 60 messages use measured, variable-height virtualization with stable message keys and end anchoring; only the viewport plus an eight-row overscan is mounted. Smaller histories retain ordinary document rendering. Prepending pages preserves context, and size changes follow the end only when the reader was already near it. Virtualization is a browser rendering optimization, not permission to fetch unbounded history or evidence of service-scale capacity.

Confirmed sends are projected immediately, then merge with later webhook evidence. Idempotency is application-local; Graph trace IDs are not idempotency keys. `429` records a rejected result before updating a shared cooldown. Transport/commit uncertainty is retained and is not matched by message text/time or automatically resent.

Notifications are validated before encrypted durable enqueue; broker acceptance failure returns an error. Invalid authentication/payloads are logged and acknowledged without processing to conceal validation outcomes, as the webhook protocol requires. The projector authenticates ciphertext before decryption, clears temporary key/plaintext buffers, preserves tombstones, and schedules repair on ambiguity. Old lifecycle events cannot remove a replacement subscription. Coverage generations prevent an older repair from clearing a newly observed gap.

The notification queue uses a one-hour TTL. The projector examines at most 25 dead-letter entries per minute, schedules durable scope repair before completing expired entries, and logs retention failures. This is bounded maintenance, not a guarantee of instantaneous physical deletion during outages/backlogs. Message TTL is bounded by original creation plus seven days; tombstones, journals, outbox and operation evidence use fourteen days. Apply approved backup/key overlap, cache, diagnostic and regional-copy rules separately.

Graph operations have a linked twenty-second deadline covering token wait, headers and response body; the policy-feed request has a ten-second deadline. Streaming JSON is bounded even when `Content-Length` is absent (16 MB background Graph, 1 MB write confirmation/policy). A canceled token wait before a POST is definite non-dispatch; a transport failure after POST dispatch remains uncertain.

### Event dispatch and durable fan-out

The Delivery role hosts two independent processors. Cosmos change feed reads committed outbox records from the beginning and checkpoints its lease only after the corresponding body-free work is accepted by `graph-delivery`. It does not scan the tenant's outbox on a timer. Replicas share the durable `live-leases` container.

Delivery messages have stable IDs and one-hour broker deduplication/TTL. A page consumer conditionally claims its outbox's progress, checks at most 100 current recipients, publishes body-free hints, and saves the next cursor before enqueueing continuation work. A crash between checkpoint and enqueue is recovered by replaying the previous message, which schedules the current saved page without repeating the completed page. Failure during publication can repeat hints but cannot skip progress.

Final progress, outbox completion and the conversation's published-revision watermark commit in one logical partition. New pages retain their target revision; a fully published revision can coalesce older pending intents without labeling newer, undelivered content as published. Expired claims can be taken over; old claims cannot overwrite a newer checkpoint.

The delivery DLQ reschedules from durable progress with a fresh recovery ID, so broker duplicate detection cannot swallow an intentional repair. Invalid work is quarantined for one hour; valid body-free recovery evidence is bounded by the original intent's fourteen-day lifetime. Restoring a queue without its outbox/progress/lease state is not sufficient recovery. Per-user PubSub REST throughput and actual large-audience latency remain capacity acceptance conditions.

### Telemetry and operational definitions

The `TeamsReference.Live` meter exports through the configured Azure Monitor provider. Counters include Graph attempts/deferrals, accepted/filtered notifications, material projection changes, completed delivery pages, recipient hint attempts, write outcomes and worker failures. Dependency histograms measure graph-send/background, intake and delivery-page duration, not full user-journey SLOs. Labels are bounded operational categories, not tenants, actors, resources or content.

`infra\modules\live-monitoring.bicep` defines queue backlog/dead-letter metric alerts, a worker-failure log alert and an operations workbook. Use `Deploy-Live.ps1 -EnableMonitoring` with reviewed `logsWorkspaceId` and backend `insightsConnectionString` parameters. `actionGroupId` is optional and must identify an approved existing notification destination; no destination or subscription is created automatically. As with RBAC, omitting the provisioning switch on later releases does not delete already-created alert resources.

The queries use the workspace-based [AppMetrics schema](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/appmetrics). Validate actual ingestion, dimensions, alert noise and routing in the target environment; compiling these definitions does not demonstrate a working alert or an SLO.

For read-only public contract checks after an approved deployment:

```powershell
.\scripts\Test-DeployedApp.ps1 -PublicOrigin '<actual-live-https-origin>' `
  -TenantId '<tenant-guid>' -WebClientId '<web-client-guid>' -ExpectedMode Live
# Add -ExpectAdmission only for the explicitly enabled admission stage.
```

The command checks the live mode/role/admission contract, compiled shell, anonymous denial, disabled local login and canonical Entra redirect. It does not sign in, read Teams content or certify Graph/production acceptance.

### Fenced recovery and rollback

1. Quiesce user admission and command dispatch; drain or explicitly stop old API revisions. Keep notification intake only where its durable queue and decryption keys are available. A configuration rollout is not proof that an old in-flight POST stopped.
2. Assess state separately: operation journal/dispatch evidence, principal authority and epochs, subscription/control/coverage, source messages/tombstones, delivery progress, broker data, change-feed leases, sessions and key versions. Namespace aliases or restored content alone do not prove these states recovered.
3. Restore durable journal and authority evidence before reopening writes. If a possibly dispatched operation lost its evidence, keep writes fenced pending exact investigation; do not infer non-commit from missing history, expired leases or an empty restored queue.
4. Establish one approved writer owner/epoch and migrate conversation/policy epochs through an explicit reviewed procedure. Old windows then fail closed. Never use an epoch increment as permission to replay uncertain operations or as a replacement for missing journal evidence.
5. Restore/reconcile subscriptions, encrypted backlog/key overlap and projection coverage. Change-feed lease reset/replay is an explicit recovery operation: replaying body-free delivery work can duplicate hints, but must not issue Graph message POSTs. Conditional page progress and publication watermarks remain authoritative.
6. Run the read-only deployment checks, then separately prove token/permission use, authorized catch-up, no unfenced duplicate dispatch, state loss bounds and serving capacity. Reopen admission only after the recorded recovery approvals/gates.

Rollback uses compatible readers/consumers before producers and must retain newer schemas, journal evidence and approved old decryption keys. Image rollback does not undo a Teams send. No automatic cross-region owner election or unapproved regional provisioning is performed; the production recovery objectives require an actual approved drill.

The following **remain release acceptance gates**, not completed evidence:

| Area | Remaining evidence or implementation |
| --- | --- |
| Identity | Real grant/token use, key rotation and sync/policy-service authorization; explicit named trust rotation and user/group admission are implemented, while destructive removal and optional RSC installation remain separately approved operations |
| Policy | A deployed authoritative feed with proven history/DLP/offboarding semantics and sufficient refresh capacity |
| Persistence | Real Cosmos transaction/query/index/consistency/RU evidence; no migration of synthetic aggregates into business data |
| Delivery/control scale | Change-feed dispatch, dedicated delivery queue and page-level leases/checkpoints are implemented. Repair uses bounded scheduled work. Hot-partition, per-user fan-out, reconnect and policy-refresh capacity still need actual workload evidence. |
| Recovery | Only a configured epoch and conditional local dispatch fence exist; no demonstrated cross-region owner-election/restore workflow or safe journal-loss recovery |
| Operations | Verify the implemented metrics/workbook/alerts against actual ingestion and routing; approve cost/capacity choices and prove backup/retention, rollback and separate real-Graph/load/soak/recovery tracks |

Do not label this foundation as production-complete or enable business traffic while these gates are unresolved.
