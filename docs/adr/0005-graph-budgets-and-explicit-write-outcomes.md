# ADR 0005: Graph budgets and explicit write outcomes

| Field | Value |
| --- | --- |
| Status | Accepted for reference design; production acceptance conditional |
| Date | 2026-10-15 |
| Related requirements | FR-02, NFR-03, NFR-07, NFR-08 |

## Context

Graph applies multiple endpoint, app/tenant, conversation, and user limits. Retries, paging, independent replica limiters, and cold-cache fallback can amplify dependency demand.

The reference busy-period all-chat send demand exceeds the published 20-RPS app/tenant ceiling. A queue cannot fix sustained overload. A timeout after dispatch can occur even when Graph has created the message.

## Decision

Centralize outbound Graph policy in an adapter with shared budgets, bounded concurrency, priorities, request coalescing, deadlines, and coordinated retry behavior. Budget hydration, reconciliation, control operations, writes, and retries explicitly.

Verify bucket sharing and size steady-state traffic below ceilings. Honor `Retry-After`; do not stack independent SDK/application retry loops. Pause affected dispatch on limiter uncertainty rather than fail open.

Use application operation IDs, payload fingerprints, durable dispatch state, and a result/outbox transaction to coalesce repeat submissions. Graph `client-request-id` is a tracing identifier, not a promise of idempotent message creation.

Expose pending, confirmed, rejected, and unknown commitment state, with projection status recorded separately. UI acceptance or local fan-out never counts as Teams creation.

Do not blindly retry a possibly committed POST or replay one after regional state loss. Reconcile where possible and preserve unresolved uncertainty. The baseline has no indefinite durable send queue.

Use JSON batching only for suitable independent operations in one authorization context. Count each subrequest, inspect its status, and handle safe retries individually.

## Alternatives considered

| Alternative | Reason not selected |
| --- | --- |
| Per-replica rate limits | Aggregate demand can exceed tenant/resource ceilings |
| More registrations or regions as a quota multiplier | Not an approved capacity strategy and may evade intended limits |
| Unlimited retries or immediate retry on `429` | Amplifies load, waiting, and throttling |
| Automatic retry of any failed POST | Can create duplicate user messages after an uncertain commit |
| Queue all sends and report success immediately | Misstates delivery and hides unsustainable backlog |
| Treat a batch as one unit of API capacity | Subrequests are individually evaluated against throttling |

## Consequences

Dependency use becomes bounded and observable. Normal reads can remain independent of Graph dispatch failures.

Some valid sends/history requests will be explicitly rejected or left uncertain; they count in user-operation SLOs. Exactly-once Graph sending is not asserted.

Higher sustained write demand requires Microsoft confirmation or a product/backend change, not additional Azure compute.

## Deployment acceptance conditions

- Confirm limits and bucket relationships for actual endpoints, permissions, and resource concentration.
- Demonstrate bounded aggregate demand across replicas, retry conditions, bootstrap, and recovery.
- Demonstrate explicit throttling, safe repeat submission, uncertain commitment, and post-confirmation synchronization failure.
- Prove that retries and pagination remain inside operation accounting.
- Reject any required workload exceeding confirmed sustainable capacity.

## Links

- [Technical design: Graph bottlenecks](../technical-design.md#5-microsoft-graph-constraints-and-bottlenecks)
- [Technical design: confirmed writes](../technical-design.md#72-confirmed-write-path)
- [ADR 0004: recovery](0004-managed-azure-services-and-regional-recovery.md)
- [Graph throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits)
- [Graph throttling guidance](https://learn.microsoft.com/en-us/graph/throttling)
- [JSON batching](https://learn.microsoft.com/en-us/graph/json-batching)
