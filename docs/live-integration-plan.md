# Live Teams integration implementation plan

| Field | Value |
| --- | --- |
| Status | Implementation in progress; production admission remains gated |
| Direction selected | 2026-10-16: complete the scalable, event-driven projection architecture, not a direct-Graph-read MVP |
| Existing baseline | .NET 10 API/BFF, React frontend, simulator, and single-region Azure dev deployment |
| Architecture | [Technical design](technical-design.md), [detailed architecture](detailed-architecture.md), ADRs 0001-0008 |
| Rebuild contract | [Entra configuration and rebuild runbook](entra-configuration.md) |

## 1. Outcome and boundaries

Implement the final production-shaped paths incrementally in the existing codebase. Teams remains the system of record; Azure maintains a bounded, authorized recent-message projection. Normal application reads never call Graph. User-authored sends use delegated Graph authorization, while separately permissioned background processing maintains the projection.

Use one codebase with independently deployed API/BFF, notification receiver, projector, delivery, and control/repair roles. Do not introduce a new broker or a full event-sourcing archive. Retain Cosmos, Redis, Service Bus, Web PubSub, Key Vault, and Container Apps, with production settings determined by the existing capacity and recovery gates.

Selecting this engineering direction does not approve copying existing business content, tenant-wide consent, licence assignment, billable deployment, or a production SLO. ADR 0007/G0 remains unresolved for business data. ADR 0008 continues to describe the implemented synthetic-only dev environment; it is not superseded by this plan.

## 2. Target runtime paths

| Path | Required behavior |
| --- | --- |
| Recent read | BFF session and fresh authorization, then Redis view or bounded Cosmos query. A cache miss does not trigger Graph. Missing coverage produces an explicit preparing/unavailable state and coalesced, budgeted hydration. |
| External Teams change | Validate a rich notification and approved scope, durably enqueue its encrypted envelope, acknowledge, then decrypt/normalize/project asynchronously. |
| Committed projection change | Atomically update message/tombstone, application revision, change journal, and outbox in one Cosmos logical partition. Publish the outbox recoverably, then deliver authorized, coalesced body-free hints. |
| User send | Validate and journal the command, acquire shared permits, claim dispatch, call Graph with that user's delegated token, then atomically record known confirmation and projection/outbox changes. |
| Reconnect | Retrieve authorized changes from a bounded cursor/revision. If the journal no longer covers the gap, require a new authorized snapshot; do not initiate per-client Graph scans. |
| Bootstrap or repair | Subscribe and buffer before hydration, merge overlap, track covered ranges, and repair bounded gaps under the same shared Graph policy as other operations. |

A confirmed send uses the same projection engine immediately, without waiting for its notification. Its later notification must merge idempotently. Graph ETags are opaque; application revisions are not Graph sequence numbers. Ambiguous version evidence triggers bounded repair rather than guessed ordering.

## 3. Current implementation gaps

| Surface | Current evidence | Required change |
| --- | --- | --- |
| Runtime selection | `src\Teams.Host\Program.cs` rejects non-simulator mode, selects the simulator, and returns fixed simulator metadata | Explicit, validated live configuration; no fallback to simulator; live readiness/admission independent of fixture readiness |
| Graph transport | `GraphTransport.cs` implements delegated POSTs, but not live reads, subscription control, or uncertainty resolution | Endpoint-specific clients/token contexts, structured Graph errors, claims/consent challenges, shared budgets, and exact-evidence resolution where available |
| Persistence | `Stores.cs` stores the whole conversation aggregate and scans a tenant catalog capped at 100 fixtures | Versioned message/control/index/outbox documents, partition-local transactions, paged per-user indexes, and a controlled schema migration |
| Projection/delivery | `ProjectionWorker.cs` combines ingestion, full-catalog maintenance scans, and per-member hint delivery | Queue-driven projector, recoverable outbox dispatcher, independently scaled delivery, and bounded scheduled maintenance |
| Authorization | Fixture membership/history and application revocation are synthetic authorities | Verified membership/history/policy evidence, freshness leases, durable denies, and connection/session invalidation |
| Redis | Protected sessions/token cache, key ring, and shared send permits | Bounded recent views, entitlement leases, hydration coalescing, and coordinated outbound budgets; validate security and eviction boundaries |
| Infrastructure | One API, one worker, one notification queue, one region, and dev data-service settings | Separate role identities and scaling, queue/control resources, production data settings, recovery configuration, and least-privilege access |
| Entra | Tagged registrations and sign-in consent helpers exist; `sync` is reserved | Versioned desired state, approved live permission matrix, actual grant reconciliation, sync credential trust, drift reporting, and rebuild/rotation procedures |

Existing dev caps are not production sizing defaults. Removing the 100-conversation or 200-message checks alone is not a scaling implementation.

## 4. Work packages and dependencies

Dependencies identify implementation prerequisites, not permission to execute changes in Entra or Azure. Development can use the approved synthetic boundary; a live-data step also requires its applicable approval gates.

### Execution status

| Package | Status | Evidence / remaining gate |
| --- | --- | --- |
| W0 | Blocked for live deployment; engineering assumptions recorded | Retain the existing global-cloud, single-tenant BFF and event-driven projection. No production-data approval, authoritative entitlement integration, approved live resources/licences, sustainable write mix, cost envelope, or regional recovery acceptance has been supplied. Implement locally without granting access or provisioning. |
| W1 | In progress | Implementing versioned desired state and offline-verifiable Entra reconciliation; no directory changes executed |
| W2 | Pending | Production persistence implementation |
| W3 | Pending | Live Graph/authentication implementation |
| W4 | Pending | Rich-notification implementation |
| W5 | Pending | Outbox/read/catch-up implementation |
| W6 | Pending | Control/entitlement/lifecycle implementation and external authority contract |
| W7 | Pending | Scalable role definitions; actual sizing remains an external gate |
| W8 | Blocked for production release | G0-G7 require actual environment, Graph, capacity, and recovery evidence; local implementation cannot certify them |

| ID | Work package | Depends on | Deliverable and exit condition |
| --- | --- | --- | --- |
| W0 | Resolve deployment inputs | None | Record processing/storage approval boundary, licensed users, allowed resources, subscription strategy, entitlement authority, chat/channel write mix, regions, recovery and cost constraints. Unsupported requirements remain explicit blockers. |
| W1 | Make identity configuration reproducible | W0 | Implement the desired-state and private-inventory contracts in the Entra runbook, read-only live diff, separately approved apply, drift/ownership refusal, resume checkpoints, export, and verification. Existing simulator provisioning remains supported. |
| W2 | Introduce production persistence and contracts | W0 | Split the aggregate behind projection, operation, entitlement, index, inbox/outbox, and coverage ports. Demonstrate partition-local atomic updates, bounded reads, deduplication, tombstones, revision barriers, and schema migration. |
| W3 | Implement live Graph/authentication boundaries | W1, W2 | Delegated sends, app-only background clients, resource onboarding/mappings, explicit reauthentication, safe paging, shared attempt accounting, and known/unknown send evidence. Keep tokens backend-only and prohibit app-only user impersonation. |
| W4 | Implement rich-notification intake and projection | W1, W2, W3 | Distinct Graph callback/lifecycle routes, scope and authenticity checks, encrypted durable acceptance, certificate-version decryption, idempotent projection, lock renewal, retries/DLQ handling, and explicit unsupported-event handling. |
| W5 | Implement outbox, reads, and client catch-up | W2, W3, W4 | Recoverable outbox publication, authorized/coalesced hints, bounded Redis/Cosmos reads, minimum-revision consistency, paged user catalog, and reconnect reset/catch-up semantics. |
| W6 | Complete control, entitlement, and lifecycle recovery | W1, W3, W4, W5 | Leased subscription ownership, renewal/reauthorization, subscribe-before-bootstrap, gap repair, history/policy enforcement, revocation, key rotation, and retention across every copy. |
| W7 | Complete scaling and operational controls | W3, W4, W5, W6 | Independent role deployments, bounded concurrency, queue-age scaling, reserved control capacity, cache/coalescing controls, body-free telemetry, hot-partition/fan-out limits, and supported production infrastructure settings. |
| W8 | Complete recovery and production release | W0-W7 | Pass the applicable G0-G7 gates with separate Graph and Azure evidence, rebuild/restore evidence, writer fencing, regional-state guarantees, rollback/stop procedures, and operational ownership. |

The first end-to-end milestone is one approved chat and one channel traversing W1-W5 using the final paths. Include the minimum subscription and authorization lifecycle needed to keep that slice safe. This is an implementation milestone, not a disposable direct-read architecture or permission for broad ingestion.

## 5. Design requirements within the work packages

### Persistence and delivery

Colocate operation confirmation, projected changes, revision/journal updates, and outbox records wherever a Cosmos transaction is required. Per-user indexes and other partitions update asynchronously through recoverable work; do not imply cross-container atomicity.

Use at-least-once processing with semantic deduplication and stable internal event IDs. A delivery GUID is not a message version, and resource ID alone cannot distinguish an edit from its predecessor. Never claim a distributed transaction across Cosmos and Service Bus. An outbox dispatcher, potentially driven by Cosmos change feed, must recover from publish-before-checkpoint failure.

Separate message documents reduce aggregate rewrite cost but do not remove conversation hot keys. Validate logical-partition capacity, revision contention, history query/index cost, read barriers across SDK instances, and high-fan-out bandwidth before selecting production partitions and provisioned throughput.

Queue expiration or dead-lettering must mark affected coverage for repair. Purging failed work is not proof that it was applied. Keep message, tombstone, journal, outbox, queue/DLQ, backup, key, and diagnostic lifetimes within the approved copy inventory.

### Authorization and identities

Use the existing cookie/BFF authorization-code flow with server-side token acquisition; no SPA/OBO migration is needed. The `web` identity needs approved send scopes, not broad message-read scopes for projected reads. Graph application roles belong to the separately configured background identity.

Select a trustworthy entitlement/policy authority and measurable freshness bound. Message subscriptions or a membership cache alone do not prove current user access, visible history, offboarding, or DLP equivalence. Deny or require verification when authority is missing/expired. Recheck recipients before hints and content before reads.

Live operation must not seed fixture conversations or expose simulator controls. Keep simulator contracts and isolated capacity environments, but separate their data, users, health semantics, and release gates from live mode.

### Graph limits and write safety

Use shared budgets keyed by actual Graph application identity and applicable endpoint/tenant/resource/user scopes. Include hydration, reconciliation, subscription/control work, retries, and all pages. Reserve control headroom; a growing queue is not permission to exceed Graph capacity.

Do not multiply Graph throughput assumptions by replicas, regions, batches, or extra registrations. The current reference's all-chat busy-period demand exceeds its documented tenant/application send ceiling; settle the supported write mix and sustainable budget before accepting send SLOs.

Keep bounded command windows, operation fingerprints, conditional dispatch ownership, and independent projection status. A transport failure after dispatch remains unknown unless exact trustworthy evidence resolves it. Graph `client-request-id` is tracing, not idempotency. Never match unknown sends by similar text/time or replay them automatically after recovery.

### Operations and recovery

Expose coverage/repair age separately from last-message time, receipt-to-projection lag separately from source-to-display lag, and Graph confirmation separately from projection/delivery. Measure hint-induced reads and reconnect work.

Keep active-writer ownership and command fencing explicit across rollout and regional recovery. Restoring message content does not restore authorization, operation evidence, or broker work. Rebuild and recovery must evaluate each state family before reopening reads or dispatch.

External Teams updates are eventually consistent; no subsecond Graph delivery or complete, ordered notification log is assumed. Keep the TDD's SLOs as proposed targets until the applicable evidence exists.

## 6. Entra and infrastructure reproducibility deliverables

W1 and W7 must make a fresh environment reproducible without a transcript or copied portal state:

| Artifact | Responsibility |
| --- | --- |
| Versioned nonsecret environment/identity profile and schema | Tenant/subscription/environment inputs, logical identities, assignments, exact endpoint/permission matrix, consent mode, federation, callback URLs, scope/installation prerequisites, and approved key references |
| Private resolved inventory | Application object/client/service-principal IDs, users, managed-identity client/principal/resource IDs, actual grants/bindings, subscription IDs/expiry, readiness evidence, and profile version/hash; no credential plaintext |
| Read-only planner and verifier | Resolve owned objects and permission IDs from the target tenant's resource principal, compare desired declarations with actual grants/trust, report missing/unexpected configuration, and gate live admission |
| Explicit apply/rotation operations | Separate directory creation, user/licence assignment, delegated consent, app-role consent, federation rebinding, and access removal; checkpoint partial progress without silently widening or deleting access |
| Deployment outputs and release mapping | Export every role's identity IDs and actual public/callback origins; generate approved runtime settings and workflow variables without reusing stale IDs |
| Rebuild/restore evidence | Cover same-tenant Azure rebuild, lost inventory, fresh tenant, principal/key rotation, fresh sign-in, notification renewal, and retained operation/authority state |

These are planned contracts, not existing scripts or a claim that Bicep manages Entra consent. The [Entra runbook](entra-configuration.md) identifies the available commands and their limits.

## 7. Acceptance and rollout

Use the [existing G0-G7 acceptance gates](technical-design.md#113-acceptance-gates), rather than a second set of numerical SLOs.

| Evidence track | Required coverage |
| --- | --- |
| Identity/rebuild | Correct tenant and ownership, two-pass no-op apply, drift refusal, partial resume, declarations versus actual grants, changed principal IDs, credential/key rotation, lost-inventory reconstruction, and fresh sign-in/token use |
| Correctness | Duplicate/out-of-order notifications, delayed edits/deletes, conflicting source evidence, tombstone expiry, bootstrap overlap, queue/outbox replay, coverage gaps, and uncertain sends |
| Authorization | Cross-user isolation, restricted history, source-policy changes, expired authority, offboarding, connection closure, and restored-state freshness |
| Real Graph | Approved licensed resources, selected permission/version support, delegated creation IDs, notifications, lifecycle renewal, paging, sustainable budgets, and bounded repair |
| Azure capacity | Representative simulator workload through production authorization and final worker topology; cache failures, hot partitions, crypto throughput, high fan-out, and reconnect amplification |
| Recovery/operations | Queue and state restoration, writer fencing, interrupted deployments, rollback compatibility, rebuild without unsafe replay, regional capacity, alerts, ownership, and cost/stop controls |

Do not direct application stress traffic at Graph. Roll out only within approved resources and budgets, with live admission initially disabled. Enable callbacks, synchronization, coverage, authorization, then user traffic through explicit readiness gates. Stop or fence the affected path on expired authority, limiter uncertainty, subscription/key failure, or missing operation evidence; never relax security or switch to simulator to appear healthy.

## 8. Documentation maintenance

| Document | Maintenance rule |
| --- | --- |
| This plan | Update work-package status and evidence references as implementation lands; do not mark planned outcomes complete from design alone |
| Entra configuration runbook | Keep the desired-state contract, command behavior, runtime settings, grant matrix, and rebuild/rotation order aligned with scripts |
| Dev guide and ADR 0008 | Preserve the current synthetic deployment boundary until a separately approved live rollout changes it |
| Technical/detailed architecture and relevant ADRs | Update actual partition, consistency, subscription, credential, scaling, and recovery choices; record material departures with an ADR |
| Testing and validation evidence | Add production-path scenarios and measured results separately from existing local/simulator/public-smoke evidence |

No live code, provisioning, consent, licence assignment, commit, or push is performed by publishing this plan.
