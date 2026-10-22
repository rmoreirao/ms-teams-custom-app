# ADR 0012: Session content reads and revision convergence

## Status

Accepted for source implementation. Supersedes only the Strong/SessionPrimary
read-profile decision in [ADR 0009](0009-live-projection-runtime-boundaries.md).
Cloud configuration migration and real-service acceptance are separate gates.

[ADR 0013](0013-availability-first-reads-and-bounded-hydration.md) supersedes only
the source-coverage display prerequisite and lag-only browser-retention policy
below. The original decision is retained as history; Session consistency,
critical current reads and send-safety guarantees remain in force.

## Context

Chat content can tolerate bounded projection lag. Routing every message lookup
through a primary-replica procedure adds a custom consistency mechanism where
native Session reads suffice. However, a displayed revision is not an SDK session
token, and weaker content reads must not weaken authorization or duplicate-send
protection. A reader behind a browser cursor does not prove journal loss.

## Decision

Require exactly `readConsistency: "Session"`. Missing settings and legacy profile
names fail validation; there are no aliases or selectable Strong content path.
Use the existing Gateway Cosmos client, Session requests and the approved
single-readable/single-writable-region account with Session or Strong account
policy. Do not change account consistency, partitioning or data schemas.

`IPartitionDocuments.ReadAsync` is an ordinary SDK point read.
`ReadCurrentAsync` explicitly executes the read-only `readCurrentV1` procedure
and validates identity, ETag and session evidence without fallback.

Message display/delta hydration uses ordinary reads and bounded SDK queries.
Current reads remain for grants, revocation, exact-pair evidence, parent
eligibility, metadata containing security epochs/coverage, operations, projection
merge/tombstone decisions, delivery progress and subscription/repair control.
Do not split metadata into more documents merely to force weaker reads.
These current reads can also advance the client's partition session barrier;
ordinary content therefore often sees their progress without a primary lookup
for each message. Independent clients still do not share tokens automatically.

Keep transactional projection/change/outbox commits, conditional operation
creation and dispatch claims, ETags, writer fencing and unknown-send semantics.
Read consistency is not source freshness or transaction atomicity.

## API and browser contract

Use explicit `503 projection_behind` for a reader behind a requested revision or
temporarily invisible changed content. A future cursor is not a reset. Delta
hydration checks the persisted message document revision against the latest
journal change for that message/context, including repeatedly edited messages.
Genuine journal expiry/gaps/size limits retain authorized snapshot reset.

Successful reads and lag errors carry verified `access` metadata: `historyFrom`,
`validUntil` and `epoch`. The deadline is capped by both the authority lease and
source coverage; no read renews either. Thread requests validate the current
parent before returning lag authority. Unknown, revoked or expired authority and
unverified coverage do not become lag with retention permission.

The browser never accepts a response below its highest outstanding/loaded
revision. On explicit lag it may retain its already loaded view only with valid,
same-epoch access, applying current history and retention restrictions first.
It removes messages at their seven-day retention boundary and clears content at
the verified deadline or on authorization/coverage failure.
No network error grants continued access. Original send outcomes and drafts
remain separate from projection convergence.

In-flight reads are single-flight; hints advance the target without aborting and
restarting those reads. Recovery uses the existing bounded backoff sequence, with
bounded jitter for replica lag and a fixed recovery budget. Hints do not restart an exhausted budget;
manual retry or successful recovery resets it. No Graph fallback, new polling
transport, shared-token subsystem or automatic uncertain-send replay is added.

## Migration and cleanup

Move each affected workload's image and explicit Session settings together under
the existing admission/writer-fencing safeguards. Legacy configuration must be
refused by ordinary image-only release preflight, not silently normalized.
Preserve previous image/configuration pairs for rollback outside the new runtime.
Do not delete procedures, reader permissions, journals, queues, identities,
subscriptions, source resources or pending work as incidental cleanup.

Remove the blanket read switch, obsolete profile branches and superseded probe
names after regression coverage is established. Keep historical ADR/evidence and
stable existing validation-container names. See the
[migration runbook](../live-runtime.md#session-configuration-migration).

## Verification and consequences

Deterministic independent-reader tests cover stale/missing content, access
revocation, epoch fencing, error evidence and procedure failure. Browser tests
cover monotonic revisions, history restriction, expiry, bounded retries and
stale response cancellation. Existing transactional and send-safety regressions
remain required.

The opt-in real Cosmos probe verifies bounded convergence with independent
Gateway clients and current revocation/fencing. It sends no Graph messages.
Measure procedure counts, RU/latency and catch-up behavior in a separately
approved deployment; offline success is not a measured performance gain, source
policy acceptance, capacity proof or regional-recovery certification.
