# Live runtime configuration and rollout

**Status: production `Live` implementation available; its activation and production acceptance remain gated.** The live paths have local contract/correctness coverage. No production app-only message grants, Strong-consistency platform or business-content processing were activated. Separately, the user-approved [GraphPilot basic mode](graph-pilot.md) is deployed on existing infrastructure and passed three users' real delegated Teams journeys; its consent/deployment does not satisfy the production gates below.

Follow the [implementation status](live-integration-plan.md#execution-status) and [Entra rebuild runbook](entra-configuration.md). G0, a trustworthy policy authority, licensed/approved resources, actual Graph endpoint support/budgets, capacity, and recovery evidence still gate release.

## Runtime and storage boundaries

| Role | Entry points and responsibilities | Required data access |
| --- | --- | --- |
| `Api` | Cookie/OIDC BFF, guarded projected reads, paged catalog, delegated sends, operation status, authenticated PubSub admission | Projection read/write for commands; authority read; Redis session/cache/permits; session-protection key; PubSub |
| `Receiver` | `/callbacks/graph/notifications` and `/callbacks/graph/lifecycle`; validation challenge, signed rich-token/client-state checks and encrypted durable acceptance | Control-state read; notification queue send; no message/authority writes, private decryption key or Graph credentials |
| `Projector` | Lock-renewing Service Bus consumption, authenticated decryption, conditional merge/tombstones, repair scheduling and bounded dead-letter retention | Projection/control read/write, queue send/receive, notification-key decrypt; no authority writes or Graph credentials |
| `Delivery` | Cosmos change-feed outbox dispatch, broker-driven leased fan-out pages, coalesced body-free hints and current-recipient authorization | Projection/checkpoint and change-feed lease read/write; authority read; delivery queue send/receive; PubSub |
| `Control` | Leased subscription ownership/renewal, bootstrap/hydration, bounded verification/repair and authoritative policy refresh | Projection/control/authority read/write; shared Graph permits; sync federation; approved policy-service access; PubSub revocation |

Use independent instances of the same image. `All`, fixture roles, local credentials, and memory persistence are rejected in live mode. There is no automatic fallback to the simulator, and live mode exposes no simulator mutation endpoints.

`infra\live.bicep` creates five distinct user-assigned identities, three state containers (`live-state`, `live-authority`, `live-control`), a separate `/id`-partitioned `live-leases` container, `graph-notifications` and `graph-delivery` queues, private Front Door origins/WAF and role-specific workloads/access. It targets an **existing dedicated private platform**; it does not create or resize its Cosmos account, VNet/private endpoints, Service Bus namespace, Redis, PubSub or Key Vault. The dev serverless/session-consistency platform is not a production sizing template.

The live Cosmos client requests **Strong consistency** so that authorization, message changes and minimum-revision checks do not rely on unrelated per-client session tokens. The platform must support and be configured for that consistency. `Deploy-Live.ps1` refuses a Session-consistency account; it does not change consistency automatically. See [ADR 0009](adr/0009-live-projection-runtime-boundaries.md).

Messages are separate documents, not aggregates or per-recipient copies. Projection, change-journal and outbox updates share a conversation partition; authority and control are separate containers. Data-plane roles prevent the API/projector/delivery identities from rewriting authoritative access. Catalog updates across partitions are recoverable and never replace a fresh source-membership check.

## Configuration

The template supplies `App:GraphMode=Live`, an independent `App:Role`, tenant, actual role identities, public callback origins, Azure endpoints and backend credential bindings. Supply the remaining nonsecret `LiveOptions` through `Live:Configuration` JSON or the corresponding `Live` configuration section. Unknown properties in the JSON form are rejected.

Start from `infra\parameters\live.parameters.example.json`, stored privately after replacing its intentionally invalid placeholders. It contains public encryption material and protected references, **never a PFX, private key, password, token or client secret**.

| Setting | Rule |
| --- | --- |
| `approvalReference` | Recorded deployment-specific data/resource approval; not a substitute for that approval |
| `writerEpoch` | Positive command-ownership epoch matching persisted conversation and policy evidence; changing it requires a fenced migration |
| `resources` | Unique URL-safe logical IDs (1-128 letters/digits/`_`/`.`/`-`), kind/title and actual chat/team/channel IDs. Reads are restricted to this allowlist and user authority. A logical ID cannot silently be retargeted to a different Graph conversation; use a new ID or an explicit fenced migration. |
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

Evidence leases cannot exceed five minutes, move backwards, or be extended using the same verification evidence. Missing/expired/conflicting authority fails closed. A removal must be represented by `active=false` with fresh evidence; omissions never extend old leases. The feed must revisit all active entries before lease expiry, page fairly, include policy/history/offboarding constraints, and prove its upstream propagation bound. If it cannot support the product's requirements at the selected scale, W0/W6 remain blocked.

The same authority version cannot be reused with a changed lease, policy or access decision at the same verification timestamp. Revocation closes existing PubSub connections; every subsequent read/send rechecks authority. Content-specific DLP/visibility rules that cannot be represented by this resource/history contract require an explicit contract extension, not a claim of equivalence.

## Staged deployment and rebuild

Directory mutations and billable provisioning require approval. These examples document the commands; do not execute them against a tenant merely to validate local code.

1. Resolve G0, target platform/network/retention requirements, licences/resources, policy authority, budgets, credentials and recovery constraints. Prepare the private live parameter/profile files.
2. Run `Deploy-Live.ps1` in default what-if mode against the selected **existing private Strong-consistency platform**. Only after approval, use `-Apply -ProvisionRbac` to establish role identities, containers, queue and edge without workload traffic.
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

`/health/live` is process health. `/health/ready` is bounded role/onboarding readiness, not a scan/certification of every resource or production SLO; each content read enforces its own coverage/freshness. Readiness alone never approves user admission.

For same-tenant rebuilds, reuse verified owned registrations/grants, but reconcile actual principal IDs, trust, callbacks, runtime allowlists and RBAC. A changed named FIC requires the explicit `-ApproveTrustRotation` path and is rechecked immediately before mutation; unlisted FICs and permission removal still require separate review. For new tenants, recreate tenant-local objects/consent/installations/licensing and resource mappings. Preserve authority, operation, subscription, change-feed lease, delivery-progress and key-version evidence separately from messages; rehydration does not prove that an unknown POST failed.

## Correctness, retention and current limits

Recent reads use Redis revision-keyed pages or bounded Cosmos queries; there is no Graph cache-miss fallback. The shared page is filtered by current history authority. Cache loss is logged and falls back only to Cosmos. Changes are journaled with projection updates; an expired/oversized catch-up gap requires a new authorized snapshot.

The catalog is fetched in bounded authorized pages, with a protected continuation header and explicit **Load more conversations** control. Current text filtering searches the loaded catalog pages; it is not a server-side full-history search index. New envelope and storage records carry schema version 1; unsupported versions fail explicitly.

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
