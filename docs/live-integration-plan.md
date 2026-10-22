# Live Teams integration implementation plan

| Field | Value |
| --- | --- |
| Status | Bounded three-user Azure Live rollout deployed and verified end to end; production acceptance remains gated |
| Last implementation update | Availability-first source increment; dated cloud milestones below remain historical evidence |
| Current hosted validation | Five isolated service-authenticated roles now run the bounded Dev scope. Earlier two-app notification/browser/send evidence remains historical; Session/availability-first rollout and fresh/unattended acceptance are separate. See [current evidence](dev-validation.md) and [runtime status](live-runtime.md). |
| Direction selected | 2026-10-16: complete the scalable, event-driven projection architecture, not a direct-Graph-read MVP |
| Current source baseline | Live-only .NET 10 API/BFF, React frontend and single-region Development; Simulator/GraphPilot runtimes removed, mocks confined to tests |
| Architecture | [Technical design](technical-design.md), [detailed architecture](detailed-architecture.md), [current ADRs](adr/README.md) |
| Rebuild contract | [Entra configuration and rebuild runbook](entra-configuration.md) |
| Implemented runtime | [Configuration, rollout, policy contract and remaining limits](live-runtime.md) |

## 1. Outcome and boundaries

Implement the final production-shaped paths incrementally in the existing codebase. Teams remains the system of record; Azure maintains a bounded, authorized recent-message projection. The API serves Cosmos/cache content, not direct Graph bodies. A genuinely cold view can request durable, bounded Control-owned Graph hydration and wait briefly for an authorized Cosmos reread. User-authored sends use delegated Graph authorization, while separately permissioned background processing maintains the projection.

Use one codebase with independently deployed API/BFF, notification receiver, projector, delivery, and control/repair roles. Do not introduce a new broker or a full event-sourcing archive. Retain Cosmos, Redis, Service Bus, Web PubSub, Key Vault, and Container Apps, with production settings determined by the existing capacity and recovery gates.

Selecting this engineering direction does not approve copying existing business content, tenant-wide consent, licence assignment, billable deployment, or a production SLO. ADR 0007/G0 remains unresolved for business data. ADR 0008 is historical for runtime selection: Live-only Development now retains the approved test-data boundary and private Sweden topology without shipping a simulator.

## 2. Target runtime paths

| Path | Required behavior |
| --- | --- |
| Recent read | Current authority/history/parent/epoch guards, then revision-keyed Redis or bounded Cosmos. Redis miss alone does not trigger Graph. Unhealthy coverage does not hide available content; a genuinely cold view requires durable coalesced Control hydration and returns usable content or typed pending after a bounded wait. Known-empty needs real evidence. |
| External Teams change | Validate a rich notification and approved scope, durably enqueue its encrypted envelope, acknowledge, then decrypt/normalize/project asynchronously. |
| Committed projection change | Atomically update message/tombstone, application revision, change journal, and outbox in one Cosmos logical partition. Publish the outbox recoverably, then deliver authorized, coalesced body-free hints. |
| User send | Validate and journal the command, acquire shared permits, claim dispatch, call Graph with that user's delegated token, then atomically record known confirmation and projection/outbox changes. |
| Reconnect | Retrieve authorized changes from a bounded cursor/revision. If the journal no longer covers the gap, require a new authorized snapshot; do not initiate per-client Graph scans. |
| Bootstrap or repair | Subscribe and buffer before hydration, merge overlap, track covered ranges, and repair bounded gaps under the same shared Graph policy as other operations. |

A confirmed send uses the same projection engine immediately, without waiting for its notification. Its later notification must merge idempotently. Graph ETags are opaque; application revisions are not Graph sequence numbers. Ambiguous version evidence triggers bounded repair rather than guessed ordering.

## 3. Simulator baseline and implementation scope

This table records the historical baseline used to scope the work, not supported source files or commands. Simulator, GraphPilot, old aggregate stores/worker, fixture jobs and local credentials have since been removed. The execution-status table below and current dev guide describe supported behavior.

| Surface | Simulator baseline | Required change |
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

**Current consistency source:** [ADR 0012](adr/0012-session-content-reads-and-revision-convergence.md)
replaces the historical Strong/SessionPrimary operating choices below with one
explicit Session content contract and targeted current reads. Revision lag is
distinct from lost history or invalid authority. Existing live settings
need the [reviewed configuration migration](live-runtime.md#session-configuration-migration);
the earlier cloud evidence does not establish deployment of this increment.

**Availability-first source:** [ADR 0013](adr/0013-availability-first-reads-and-bounded-hydration.md)
removes the coverage display gate, preserves valid authorized history through
operational failures and adds typed cold loading through bounded Control hydration.
Durable intake, conditional merge/outbox, revision-keyed cache and change-feed
delivery remain the convergence path. This accepts delayed missed edits/deletions,
not stale authority or unlimited retention. The new queue/scoped grants, rebuilt
combined image, alert routing and real-service acceptance are separately gated;
the earlier Session-only image is not this release package.

**Azure vertical slice delivered:** the existing-platform `DelegatedSynthetic` profile now passes real signed Graph notifications through Service Bus, Key Vault decryption, Cosmos projection/outbox, delivery and Web PubSub into two open browser feeds. It uses the existing serverless Session account with SessionPrimary and the existing two app sizes. All three users, five scoped resources, exact-pair denials and restart persistence were checked. The bounded adapter retains original delegated sessions for at most eight hours; it does not supply unattended production authorization, organizational policy/DLP equivalence, dynamic production onboarding, 200,000-user capacity or regional recovery. The broader production work-package gates below remain separate from this completed milestone.

**Basic validation delivered:** after the user deferred additional infrastructure, the existing API was updated to delegated GraphPilot mode. Alex, Sam and Jordan each completed fresh password/TOTP and actual Teams chat/channel/post/reply creation plus read-back. This validates the agreed basic functionality, not production `Live` acceptance. ADR 0010 records the scope change; the stronger work-package gates below remain intentionally separate.

| Package | Status | Evidence / remaining gate |
| --- | --- | --- |
| W0 | Deployment gate; engineering decisions recorded | Retain the global-cloud, single-tenant BFF and event-driven projection. Deployment-specific G0 approval, authoritative policy endpoint, resource/licence evidence, sustainable write mix, cost envelope and recovery acceptance must be supplied before activation. Code work did not grant access or provision resources. |
| W1 | Implemented locally; tenant evidence pending | Schema/example and Plan/Apply/Verify/Export, user/group admission, exact consent/federation checks, explicit opt-in trust rotation with preflight-race refusal, partial resume, rediscovery and no-write WhatIf tests. Licence purchases/assignment and optional RSC installation remain approval/endpoint-specific steps; no real grants applied. |
| W2 | Implemented locally; updated Session migration separately gated | Separate projection/authority/control and lease documents, bounded catalog/message paging, immutable logical-to-Graph mapping, Session content and targeted current reads, source leases, tombstones, change journal, atomic outbox and dispatch-epoch claims. Real Cosmos query/transaction/index/hot-key behavior remains an environment gate. |
| W3 | Implemented and verified locally; real Graph acceptance pending | Delegated writes and background-only clients, opaque paging guards, whole-operation deadlines and payload bounds, shared permits/cooldowns, explicit interaction/unknown outcomes. Pre-token cancellation is definite non-dispatch; post-dispatch uncertainty never triggers automatic replay. Real consent/Conditional Access and budgets remain gates. |
| W4 | Implemented and verified locally; service acceptance pending | Signed publisher/tenant/audience validation, scope-filtered encrypted durable intake, authenticated decryption, lock-managed consumer, stale-lifecycle refusal, schema checks and repair-before-DLQ cleanup. Real notification versions, lock loss, key rotation and webhook deadlines remain environment checks. |
| W5 | Implemented locally; service/capacity evidence pending | Cosmos change-feed outbox dispatch with lease container, duplicate-safe Service Bus delivery queue, conditional page claims/checkpoints and atomic completion/revision watermark. Includes replay/continuation-loss/claim-expiry/coalescing tests; no tenant outbox polling on the normal path. Real broker/change-feed and high-fan-out capacity still require evidence. |
| W6 | Implemented policy-feed integration and lifecycle; deployment binding pending | Leased renewal/reauthorization, exact-correlated uncertain subscription recovery, bounded repair including replies, generation-fenced coverage, bounded-concurrent policy refresh, key-version references and fail-closed authenticated policy adapter. ADR 0009 records the unattended choice not to invent Graph-only history/DLP guarantees. Deployment must supply the approved service and prove rotation/restore behavior. |
| W7 | Implemented and verified locally; operational acceptance pending | Five scoped identities/workloads, change-feed leases, queue scalers, private edge/WAF, gated deployment, CI, body-free OpenTelemetry counters/timings, opt-in queue/worker alerts and operations workbook. Actual sizing, metric ingestion/routing and SLO/capacity evidence remain deployment gates. |
| W8 | Live-only release/readiness procedures implemented; new rollout separately gated | Existing-two-app image-only release, both-target preflight, configuration preservation, exact ready revisions and read-only Live smoke. Offline test harnesses replace simulator-dependent checks. Production G0-G7/capacity/recovery acceptance remains separate. |

| ID | Work package | Depends on | Deliverable and exit condition |
| --- | --- | --- | --- |
| W0 | Resolve deployment inputs | None | Record processing/storage approval boundary, licensed users, allowed resources, subscription strategy, entitlement authority, chat/channel write mix, regions, recovery and cost constraints. Unsupported requirements remain explicit blockers. |
| W1 | Make identity configuration reproducible | W0 | Implement desired-state/private-inventory contracts, live read-only diff, separately approved apply, drift/ownership refusal, resume/export/verification. Rebuild directly to Live; no intermediate simulator/pilot deployment. |
| W2 | Introduce production persistence and contracts | W0 | Split the aggregate behind projection, operation, entitlement, index, inbox/outbox, and coverage ports. Demonstrate partition-local atomic updates, bounded reads, deduplication, tombstones, revision barriers, and schema migration. |
| W3 | Implement live Graph/authentication boundaries | W1, W2 | Delegated sends, app-only background clients, resource onboarding/mappings, explicit reauthentication, safe paging, shared attempt accounting, and known/unknown send evidence. Keep tokens backend-only and prohibit app-only user impersonation. |
| W4 | Implement rich-notification intake and projection | W1, W2, W3 | Distinct Graph callback/lifecycle routes, scope and authenticity checks, encrypted durable acceptance, certificate-version decryption, idempotent projection, lock renewal, retries/DLQ handling, and explicit unsupported-event handling. |
| W5 | Implement outbox, reads, and client catch-up | W2, W3, W4 | Recoverable outbox publication, authorized/coalesced hints, bounded Redis/Cosmos reads, minimum-revision consistency, paged user catalog, and reconnect reset/catch-up semantics. |
| W6 | Complete control, entitlement, and lifecycle recovery | W1, W3, W4, W5 | Leased subscription ownership, renewal/reauthorization, subscribe-before-bootstrap, gap repair, history/policy enforcement, revocation, key rotation, and retention across every copy. |
| W7 | Complete scaling and operational controls | W3, W4, W5, W6 | Independent role deployments, bounded concurrency, queue-age scaling, reserved control capacity, cache/coalescing controls, body-free telemetry, hot-partition/fan-out limits, and supported production infrastructure settings. |
| W8 | Complete recovery and production release | W0-W7 | Pass the applicable G0-G7 gates with separate Graph and Azure evidence, rebuild/restore evidence, writer fencing, regional-state guarantees, rollback/stop procedures, and operational ownership. |

The first end-to-end milestone is one approved chat and one channel traversing W1-W5 using the final paths. Include the minimum subscription and authorization lifecycle needed to keep that slice safe. This is an implementation milestone, not a disposable direct-read architecture or permission for broad ingestion.

### Local verification evidence

On 2026-10-19, the current shared checkout passed 89 Release backend tests, the frontend build/type-check and six component tests, and all ten isolated local browser scenarios. The browser run used a task-owned free port because 5367 was occupied; its server shut down afterward. A concurrently added fixture test's locator was corrected to the existing "Opening team Group chat" accessible name without changing fixture access semantics.

The management script/module parser and offline bootstrap, sign-in consent, identity reconciliation, live-deployment and deployment-contract suites passed. All three deployment templates, including nested monitoring definitions, compiled with the available Azure CLI Bicep 0.46.1; CI remains pinned to 0.48.1. The modified CI workflow passed actionlint 1.7.7 (external shell/Python analyzers disabled). These are current-checkout results, not proof of real tenant, broker/Key Vault/Cosmos, capacity, recovery or remote-workflow acceptance.

Current code is staged for an independently approved live rollout; this task did not change the hosted deployment. Configuration and production-acceptance gates are listed explicitly in the [live runtime guide](live-runtime.md#correctness-retention-and-current-limits). Local code completion does not mark G0-G7, authority binding or live rollout as passed.

The repository implementation selects the explicit policy-feed boundary in ADR 0009. It does not silently substitute an invented Graph-membership-only authority when no organizational policy service has been supplied. This resolves the code-side integration choice while preserving the deployment's obligation to provide and approve that authority before live activation.

### End-state app integration follow-up (2026-10-19)

The frontend now uses targeted/coalesced hints, authorized message deltas, minimum-revision snapshots, history filtering, command-window renewal and reconnect/foreground catch-up. Normal delta updates preserve loaded earlier pages; reset rebuilds the loaded page count. Push transports reconcile on a jittered 4-6-minute interval rather than polling every 30 seconds. The pilot's negotiated poll remains truthful and unchanged.

Production approved-contact discovery is opt-in through `LiveOptions.directChats`. Both participants need current policy authority and the source must separately prove the actual one-to-one type/exact pair; configuration and two individual grants are not sufficient. Opening reuses the approved projected chat without Graph calls or sends. This removes the previous unconditional production discovery disablement without inventing directory permissions. It does not implement arbitrary recipient search, new production chat creation or dynamic resource onboarding. Supported rich formatting uses a maintained allowlist sanitizer; attachments/embedded media have explicit Teams-only notices/source links. Histories above 60 messages use measured variable-height virtualization with stable keys and end anchoring.

The end-state plan remains only partially deliverable without external inputs:

| Milestone | Repository increment / remaining prerequisite |
| --- | --- |
| Production contract | G0 storage/copy/retention approval, admitted population, discovery boundary, policy authority and cost/recovery inputs remain unresolved; none was inferred from an implementation request |
| Event-driven vertical slice | Existing live adapters plus a new complete controlled-dependency journey for chat/post/reply; actual Graph, broker, Key Vault, Cosmos and PubSub evidence remains required |
| Browser live behavior | Implemented targeted synchronization, preserved pages, stale-history clearing, explicit failure/reconnect and background reconciliation; real-service freshness acceptance remains required |
| Production New chat | Current-authority discovery/open of approved existing pairs implemented; automatic creation/registration/subscription/policy onboarding and broader paged directory discovery remain engineering work after their scope/authority contract is approved |
| Content/UX | Allowlisted rich formatting, safe links, explicit attachment boundaries, variable-height virtualization and responsive/keyboard journeys implemented; attachment transfer, arbitrary cards and full-history search are not implemented or claimed |
| Bounded rollout | Use explicit Session configuration and targeted current reads on the existing account. Coordinate image/configuration migration; source completion is not cloud acceptance or permission to change admission |
| Final scale/recovery | Sustainable Graph write mix, 200,000-user capacity, full regional recovery implementation/drill and G0-G7 evidence remain unproven |

Do not describe this increment, a passing local suite or a transport's `Live` label as completion of all seven product milestones. See the [runtime contracts](live-runtime.md#browser-synchronization-and-approved-contact-discovery) and [test boundaries](testing.md#end-state-synchronization-increment).

### Session-consistency correction

Historically, the Strong-only shortcut was replaced by SessionPrimary, which
routed all point reads through the primary procedure. ADR 0012 now removes both
runtime choices: content uses native Session, critical reads remain current, and
legacy configuration must migrate explicitly. Provisioning retains procedures and
scoped reader execution without changing account consistency/capacity. Actual
private-service verification remains opt-in; previous deployment evidence is
historical rather than proof for the new image/settings.

## 5. Design requirements within the work packages

### Persistence and delivery

Colocate operation confirmation, projected changes, revision/journal updates, and outbox records wherever a Cosmos transaction is required. Per-user indexes and other partitions update asynchronously through recoverable work; do not imply cross-container atomicity.

Use at-least-once processing with semantic deduplication and stable internal event IDs. A delivery GUID is not a message version, and resource ID alone cannot distinguish an edit from its predecessor. Never claim a distributed transaction across Cosmos and Service Bus. An outbox dispatcher, potentially driven by Cosmos change feed, must recover from publish-before-checkpoint failure.

Separate message documents reduce aggregate rewrite cost but do not remove conversation hot keys. Validate logical-partition capacity, revision contention, history query/index cost, read barriers across SDK instances, and high-fan-out bandwidth before selecting production partitions and provisioned throughput.

Queue expiration or dead-lettering must mark affected coverage for repair. Purging failed work is not proof that it was applied. Keep message, tombstone, journal, outbox, queue/DLQ, backup, key, and diagnostic lifetimes within the approved copy inventory.

### Authorization and identities

Use the existing cookie/BFF authorization-code flow with server-side token acquisition; no SPA/OBO migration is needed. The `web` identity needs approved send scopes, not broad message-read scopes for projected reads. Graph application roles belong to the separately configured background identity.

Select a trustworthy entitlement/policy authority and measurable freshness bound. Message subscriptions or a membership cache alone do not prove current user access, visible history, offboarding, or DLP equivalence. Deny or require verification when authority is missing/expired. Recheck recipients before hints and content before reads.

Live operation must not seed fixture conversations or expose simulator controls. Simulator and pilot contracts/runtimes are removed. Keep offline controlled dependencies inside tests only, with separate real-Teams evidence and approval gates.

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

The desired-state schema/reconciler, user/group assignments, explicitly approved named-trust rotation, directory checkpoints, gated workload template, change-feed/delivery state, operational definitions and deployment-output export are implemented. Licence purchases/assignment, unlisted access removal, actual acceptance evidence and regional restore execution remain separately approved operations. Bicep does not manage Entra consent. The [Entra runbook](entra-configuration.md) identifies exact commands and limits.

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

The repository-side live foundation has been implemented. No Azure provisioning, tenant consent, licence assignment, business-content access, commit, or push was performed as part of this implementation.
