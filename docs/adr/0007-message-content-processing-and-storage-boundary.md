# ADR 0007: Message-content processing and storage boundary

| Field | Value |
| --- | --- |
| Status | Proposed; no deployment privacy profile selected or approved |
| Date | 2026-10-15 |
| Related requirements | FR-01, FR-03, NFR-02, NFR-04, NFR-05, NFR-08, NFR-09 |
| Related decisions | ADR 0001, ADR 0002, ADR 0003, ADR 0006 |
| Current reference candidate | P07: bounded full Cosmos DB projection with Redis; conditional on approval |

## Context

The reference architecture keeps Teams authoritative and copies a recent working set into Azure to isolate interactive reads from Graph latency, pagination, and throttling.

That working set can contain personal and sensitive data. The data-processing footprint also includes Redis, encrypted rich notifications, queue/dead-letter work, outbox/operation records, indexes, backups, diagnostics, exports, and client storage. Identifiers, membership, timestamps, previews, and linkable fingerprints can remain personal data even when message bodies are removed.

A restriction on durable message copies differs from a restriction on memory caching, backend plaintext processing, a specific database product, or all personal data outside Microsoft 365. These boundaries lead to different architectures and cannot be resolved merely by choosing encryption or a shorter retention period.

Graph permission consent and licensing authorize API use; they are not privacy approval for an additional retained copy. The approved processing/storage boundary must precede deployment-specific content handling and performance commitments.

## Decision

**Propose making an explicit, approved privacy profile a prerequisite for enabling the corresponding real-data processing or storage path.**

The proposal has the following rules:

1. Define the processing purpose and permitted boundary: browser-only content, transient backend handling, approved RAM caching, metadata-only persistence, minimized content, or an approved durable projection.
2. Select a pattern from the [canonical P01-P13 comparison](../detailed-architecture.md#56-privacy-and-storage-options), or a documented combination with explicit resource/classification boundaries. Record the choice per environment and approved resource scope.
3. Approve fields, resource scope, regions, retention/expiry, deletion and backup behavior, copied-data authorization, history/DLP handling, operational access, and identity-lifecycle controls.
4. Inventory all copies, including encrypted and derived records. A no-durable-body profile must not retain bodies in a rich-notification queue, failed-work record, outbox, trace, dump, or support export.
5. Treat unknown or unapproved content classification as no-store for that path. Do not silently enable another cache/store, broaden scope, or switch to a body-processing path to preserve performance.
6. Recalculate Graph demand, read/freshness SLOs, recovery, and test populations for the selected profile. A no-projection or hybrid variant cannot inherit the proposed projection's read behavior without evidence.

P07 remains the performance-oriented reference candidate, not a deployment privacy approval. This record is **Proposed** because the actual policy boundary and approved deployment selection are unresolved.

If a different pattern is selected, record acceptance and any amendments/supersession needed for ADRs 0001-0003. Do not claim that the current projection, token boundaries, or synchronization flows already implement every alternative.

## Alternatives considered

The full trade-off table remains in the detailed architecture to avoid maintaining several conflicting copies.

| Pattern family | Selection condition and consequence |
| --- | --- |
| P01-P03: no retained message bodies | Appropriate only with approved identity/metadata and content-processing paths; message reads remain Graph-dependent |
| P04-P05: ephemeral caches | Requires explicit permission for RAM copies and verified persistence, diagnostic, replication, and lifecycle behavior |
| P06: minimized projection | Limits fields/content but does not guarantee anonymity or eliminate backend processing |
| P07: bounded full projection | Strong read isolation; requires approval for the retained working set and every synchronization/operational copy |
| P08: application-layer encrypted projection | Can hide selected plaintext from the database service; an application that decrypts still processes plaintext |
| P09: resource/classification hybrid | Applies different approved paths with explicit scope and separate performance populations |
| P10-P11: another approved store/location | Changes product/hosting boundaries, not the fact that an additional copy exists |
| P12: native Teams experience | Avoids an application-owned body replica at the cost of a different UX and measurement boundary |
| P13: another supported chat backend | Changes the product/system-of-record architecture and requires independent privacy, lifecycle, and interoperability review |

Encryption at rest, customer-managed keys, pseudonymization, private endpoints, residency, and retention are complementary controls. They do not independently permit an otherwise prohibited copy or prove anonymization of free text.

## Consequences

Privacy becomes an architectural prerequisite rather than a checklist applied after Cosmos DB is chosen. A deployment can use the bounded projection only when its processing and storage purpose is approved.

Some policies can make the current performance-oriented architecture infeasible. More Graph calls, weaker warm-state recovery, additional authentication boundaries, or a native/different chat experience may be necessary.

All Teams-backed read variants retain the normal Graph write ceilings. The chosen privacy pattern does not grant more write throughput or support arbitrary API workloads.

Real-data processing cannot be enabled merely to discover whether policy approval is possible. Design and capacity experiments can continue in an approved synthetic/test scope; real test identities and metadata still need appropriate handling.

## Deployment acceptance conditions

Pass [TDD gate G0](../technical-design.md#113-acceptance-gates) before enabling the corresponding real-data path:

- Obtain recorded approval from the applicable privacy/legal, security, and data-owning roles, including an impact assessment where required.
- Record the selected pattern, processing purpose, permitted fields/resources, hosting/regions, retention, deletion/backup rules, operational access, and residual risks.
- Demonstrate that the inventory and flows match the boundary, including callbacks, queues/DLQs, outboxes, operation evidence, diagnostics, browser handling, and recovery.
- Verify authorization, history restrictions, policy changes, and lifecycle enforcement for any copied content.
- For a no-copy/no-processing variant, remove or redesign incompatible content paths rather than retaining encrypted bodies under a different label.
- Re-baseline dependency budgets, SLO populations, and recovery objectives, then provide the required G1-G7 evidence for the chosen architecture.

Approval is not implied by this ADR's existence. When the selection is accepted, update the status, chosen profile and approval evidence references. Reassess material changes in purpose, fields, resource scope, regions, retention, or processing path.

## Links

- [Technical design: privacy prerequisites](../technical-design.md#21-privacy-and-data-handling-prerequisites)
- [Detailed architecture: full option comparison](../detailed-architecture.md#56-privacy-and-storage-options)
- [Detailed architecture: selecting a privacy boundary](../detailed-architecture.md#57-selecting-and-implementing-a-privacy-boundary)
- [ADR 0001: bounded read model](0001-teams-system-of-record-and-bounded-read-model.md)
- [ADR 0002: copied-data authorization](0002-permission-separation-and-copied-data-authorization.md)
- [ADR 0003: durable synchronization](0003-rich-notifications-and-durable-synchronization.md)
- [ADR 0006: capacity and feasibility evidence](0006-slo-driven-capacity-and-feasibility-validation.md)
- [Microsoft Graph best practices and local storage](https://learn.microsoft.com/en-us/graph/best-practices-concept#storing-data-locally)
