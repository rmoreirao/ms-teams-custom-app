# ADR 0001: Teams as system of record with a bounded Azure read model

| Field | Value |
| --- | --- |
| Status | Accepted for reference design; production acceptance conditional |
| Date | 2026-10-15 |
| Related requirements | FR-01, FR-03, FR-04, NFR-02, NFR-05 |

## Context

A custom chat/feed view can require several dependent Graph pages before it has sufficient displayable content. Request-time paging exposes the UI to dependency latency and amplifies request consumption. Application connection capacity does not increase Teams endpoint ceilings.

Microsoft's embedding reference recommends caching messages and using change notifications to avoid repeated retrieval. Copied content introduces explicit authorization, retention, synchronization, and recovery responsibilities.

## Decision

Keep Teams authoritative for messages and collaboration state. Maintain an approved, bounded recent-message projection in Azure Cosmos DB and serve hot views through Azure Managed Redis.

Store message bodies by conversation, not once per recipient. Maintain the required membership/history metadata and inbox indexes. Normal recent reads use Azure data; missing coverage triggers budgeted, coalesced repair with an explicit UI state rather than synchronous unbounded Graph scanning.

Handle system/control events separately from the displayable-message index. Preserve edits, deletions, policy changes, and tombstones. Older history is a distinct workflow with its own bounds and latency target.

## Alternatives considered

| Alternative | Reason not selected as the reference default |
| --- | --- |
| Graph passthrough for every read | Retains pagination/dependency latency and scales Graph demand with reader activity |
| Short-lived cache without durable projection | Does not provide reliable warm-state, replay, coverage, or regional recovery |
| Full indefinite Teams replica | Exceeds the working-set objective and expands privacy, retention, and compliance responsibilities |
| Native Teams entry points | Valid product alternative, but not the chosen custom-view experience |
| Independent chat backend | Changes data ownership, integration, and compliance; not a transparent replacement for Teams conversations |

## Consequences

Recent-read performance can scale independently of Graph reads. Cached content can remain readable during some dependency failures, but freshness degradation must remain visible.

The projection is eventually consistent and creates additional operational/security complexity. It must not become an unapproved archive or bypass access changes.

Fast local reads do not improve Graph write capacity. This decision does not establish 200,000-user production feasibility.

## Deployment acceptance conditions

- Approve the retained working set, expiry basis, deletion, logs, queues, and backup policies.
- Demonstrate bounded bootstrap/reconciliation within real Graph budgets.
- Demonstrate access, history visibility, policy enforcement, and revocation on copied data.
- Verify recent-read and freshness targets independently.
- Reject the design if required write throughput or security semantics cannot fit confirmed dependencies.

## Links

- [Technical design: projection and interactions](../technical-design.md#7-interaction-and-synchronization-design)
- [ADR 0002: copied-data authorization](0002-permission-separation-and-copied-data-authorization.md)
- [ADR 0003: synchronization](0003-rich-notifications-and-durable-synchronization.md)
- [Microsoft embedding reference](https://learn.microsoft.com/en-us/graph/teams-embed-within-own-app)
