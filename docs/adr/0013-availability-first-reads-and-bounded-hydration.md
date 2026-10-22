# ADR 0013: Availability-first reads and bounded hydration

## Status

Accepted for source implementation. Amends the content-freshness/read-retention
policy in [ADR 0012](0012-session-content-reads-and-revision-convergence.md);
its Session consistency, current security/control reads and send guarantees remain.
Queue provisioning, grants, image/configuration rollout and real-service acceptance
are separate approval gates. This decision does not activate business-content copying.

## Context

A source-coverage deadline answers whether reconciliation has demonstrated a
healthy projection, not whether every stored message is unusable. Making that
deadline a display prerequisite hides authorized history during repair, even
when Cosmos and authorization remain available. Conversely, an empty database
query does not prove that Teams history is empty.

Redis cache availability, Cosmos replica convergence, Teams synchronization
health and current authority are different states. The change feed observes
committed projection/outbox records; it cannot recover missing source content.

## Decision

### Available content

Serve existing authorized Cosmos content without requiring healthy source
coverage. Redis is an optional, message-only, revision-keyed cache; a miss or
failure falls back to Cosmos, not directly to Graph. There is no hard
source-content age limit that hides the stored view during a synchronization
outage. Repair remains responsible for restoring source health.

This deliberately accepts that a missed source edit or deletion can remain
visible until reconciliation succeeds or the seven-day content lifetime ends.
Do not describe an available view as necessarily fresh. Source-health and
freshness SLIs/alerts remain independent from read availability.

Current source-backed membership/policy, revocation, mapping, history, parent
eligibility, epochs and retention remain mandatory. `ReadAccess.validUntil` is
bounded by the original authority lease, not source coverage. Reads cannot renew
that evidence. Recheck access after loading a page so observed history tightening
also restricts the returned rows and browser access proof.

The browser may preserve an already loaded, still-authorized same-epoch view
during operational/network failures, without a blocking synchronization panel.
It applies tighter history immediately, renews access before its verified
deadline, and removes content on expiry, revocation or an unverifiable critical
security decision. A network failure never extends access. Drafts and uncertain
send outcomes remain independent.

### Cold and partial views

Distinguish genuinely known-empty content from uninitialized content:

- Available rows return ordinary `200` pages. Partial local pages retain a
  continuation and can request optional hydration without waiting for Graph.
- Completed, matching-epoch view evidence or real prior completed coverage can
  establish initialization. Persistent body-free `read-view` markers are not
  freshness certificates and have no TTL; seven-day message retention still
  applies. Invalidating real legacy coverage preserves its original completion
  evidence, never a fabricated new verification time.
- An unknown view requires accepted durable refresh work. The API waits within
  a five-second total budget, rereading through the same authorization guards.
  If content becomes usable, the initiating request returns it. Otherwise it
  returns `202 { state: "pending", retryAfterMs: 1000, access }` and
  `Retry-After: 1`, not an empty `MessagePage`. An unknown parent has no fresh
  access proof until its eligibility is established.
- Submission failure is an explicit `read_refresh_unavailable`; accepted work
  is not claimed when the queue is unavailable. The browser automatically retries
  cold and older-page loading within its fixed 120-second recovery budget, then
  reports honest unavailability. Already loaded history stays usable while its
  authorization remains valid.

Minimum-revision lag remains `projection_behind`, not a reason to call Graph or
reset the journal. Cosmos/security-store failure is not a Graph-only fallback.

### Durable, Control-owned hydration

Use one new queue, `graph-read-refresh`, in the existing Service Bus namespace.
API has queue-scoped Data Sender; Control has queue-scoped Data Receiver. No
new Graph permission, identity, API background token or API control-container
write access is required. Synchronization-enabled startup checks queue/data-plane
access without enqueueing or consuming work.

Requests contain only approved tenant/conversation/epoch/parent/page context,
request time and schema version. Validate scope, age, cursor, mapping and epoch
before Graph. Derive source paths from the approved registry and validate every
continuation; callers cannot supply source URLs.

Broker duplicate detection and a conditional durable context lease coalesce
requests across replicas. Original redelivery is not acknowledged merely because
its old lease remains active. HTTP cancellation does not cancel accepted work.
Each job uses existing background Graph credentials/shared permits and at most
three 50-item collection pages plus an optional parent GET, within 45 seconds.
Control concurrency is capped at two. Chat source paging is creation-descending;
channels do not assume that ordering. Persist continuation after each page.

Hydration uses the existing parser and conditional projection merge, journal,
outbox and hint path. Newer tombstones/version evidence win; absence from a page
is not deletion proof. Progress/initialization writes do not bump application
revision or manufacture global healthy coverage. Ordinary reconciliation,
subscription maintenance and verification continue independently.

The queue has a five-minute TTL, expiry dead-lettering, one-minute locks and
five deliveries. The receiver renews locks and waits/abandons with bounded
backoff; it does not schedule replacement messages or need sender rights.
Poison/fenced/exhausted work is dead-lettered explicitly. There is no automatic
refresh-DLQ replay or separate expired-cursor restart state machine; inspect the
cause, restore the approved dependency and use a fresh authorized read or an
explicitly reviewed recovery. Existing broader repair remains in place.

### Cache and change feed

Cache keys include the committed projection revision. A newer revision cannot
read an older key as current; old entries expire normally. No synchronous cache
invalidation or extra read-barrier service is needed. Change feed dispatches
committed outbox work, delivery publishes body-free revision hints, and clients
fetch authorized deltas/snapshots. Hydration enters this same path after merge.

## Release, support and verification

Provision the queue and exact queue-scoped grants only after approval. The
five-role image-only release preflights their properties/access without mutation;
workloads-only deployment does not create them. A combined Session/availability
release needs a rebuilt coordinated image, reviewed settings and queue readiness.
Earlier Session-only image/evidence does not contain this increment.

Monitor refresh acceptance/coalescing/failure/progress/age, queue backlog/DLQ and
stalled repair, using checkpoint/work age rather than the last message date.
Ordinary budget deferral is distinct from authentication, poison or no-progress
failure. Configure and verify an alert destination before claiming routed
support. `ProjectionCheck` continues to require healthy coverage; visible cached
history alone cannot pass release acceptance.

Deterministic tests cover unavailable coverage with usable history, known-empty
versus pending, initiating-request hydration, cancellation, cross-replica leases,
partial/older continuations, bounded source work, parent eligibility, current
history tightening, conditional markers and version/tombstone races. API,
component and compiled-browser tests cover typed loading, retained history/drafts,
bounded retries, access renewal/expiry and malformed responses. Infrastructure
compilation and mocked release refusals do not prove real queue/Graph access,
source propagation, support routing, capacity or deployed acceptance.
