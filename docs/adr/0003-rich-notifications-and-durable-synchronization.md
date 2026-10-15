# ADR 0003: Rich notifications and durable synchronization

| Field | Value |
| --- | --- |
| Status | Accepted for reference design; production acceptance conditional |
| Date | 2026-10-15 |
| Related requirements | FR-03, FR-04, NFR-02, NFR-06, NFR-07 |

## Context

Polling Graph for every connected client ties dependency demand to audience size. Basic notifications can add a GET per event. Rich notifications carry encrypted resource data and reduce this read amplification.

Teams subscriptions have an organization-wide shared quota. Subscriptions expire, payloads may be duplicated or delayed, and slow webhook receivers can experience dropped notifications. Notifications are not assumed to form a complete, ordered replication log.

## Decision

Use rich change notifications for verified supported resources. Select the narrowest suitable approved aggregate scope instead of a subscription per browser/user.

Implement a small HTTPS receiver that validates authenticity and approved resource scope, durably enqueues accepted work in Service Bus, and responds within the webhook deadline. Do not perform Graph hydration or recipient fan-out before acknowledgment.

Process events idempotently with stable resource identities, version-aware updates, deletion tombstones, and bounded authoritative checks for ambiguity. Internal confirmed-write events use the same projection logic.

Manage expiry, renewal, reauthorization, supported lifecycle events, and certificate rotation. Subscribe and buffer before bootstrap; reconcile overlaps and maintain coverage/repair checkpoints. Run bounded reconciliation rather than relying on notifications alone.

Do not assume universal delta support or `missed` lifecycle events for Teams messages.

## Alternatives considered

| Alternative | Reason not selected |
| --- | --- |
| Per-client Graph polling | Scales dependency demand with audience size and increases throttling |
| Basic notifications plus GET for each event | Adds avoidable read demand where rich payloads are supported |
| Per-user subscription for the full target population | Does not fit the documented 10,000 shared Teams subscription limit |
| Process everything inline at the webhook | Couples acknowledgment to Graph/storage/fan-out latency |
| Notifications without reconciliation | Cannot establish complete coverage after gaps, expiry, or failures |
| Event Hubs/Event Grid delivery | Valid alternatives for a later scoped decision; not required for the initial webhook/queue pattern |

## Consequences

Ingestion scales with approved changes rather than connected users. Durable buffering isolates the receiver from projection and fan-out load.

Encryption/key management, renewal, replay, dead-letter handling, and repair add operational responsibilities. Aggregate subscriptions do not authorize copying unrelated resources or relax end-user access checks.

External Teams changes retain Graph delivery latency; the design does not promise subsecond freshness.

## Deployment acceptance conditions

- Verify subscription count, scope, payload support, permissions, and expiry for selected APIs.
- Demonstrate durable acknowledgment and explicit failure when acceptance cannot persist.
- Demonstrate duplicate/out-of-order processing, tombstone protection, bootstrap overlap, expiry, reauthorization, and rotation.
- Demonstrate repair within Graph budgets and the proposed freshness envelope.
- Validate queue-data recovery, not only namespace/alias failover.

## Links

- [Technical design: notification synchronization](../technical-design.md#73-notification-synchronization-path)
- [ADR 0005: dependency budgets](0005-graph-budgets-and-explicit-write-outcomes.md)
- [Teams message notifications](https://learn.microsoft.com/en-us/graph/teams-changenotifications-chatmessage)
- [Webhook delivery requirements](https://learn.microsoft.com/en-us/graph/change-notifications-delivery-webhooks)
- [Notification quotas](https://learn.microsoft.com/en-us/graph/change-notifications-overview#supported-resources)
