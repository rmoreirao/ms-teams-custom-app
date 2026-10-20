# Architecture decision records

These records explain the decisions behind the [technical design](../technical-design.md).

**Accepted** means selected for this reference design. It does not mean a deployment has passed security review, a proposed SLO has been achieved, or Microsoft/Azure capacity has been certified. Each record lists deployment acceptance conditions.

ADR 0007 is **Proposed**: the deployment's processing/storage boundary is not yet selected or approved. It does not automatically accept the proposed Cosmos DB content copy.

ADR 0008 records approval of a synthetic-only dev boundary. That limited approval does not accept ADR 0007 for existing Teams/business content or production processing.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-teams-system-of-record-and-bounded-read-model.md) | Accepted for reference design | Teams remains authoritative; Azure holds a bounded recent-message projection |
| [0002](0002-permission-separation-and-copied-data-authorization.md) | Accepted for reference design | Separate identity responsibilities and explicitly authorize copied content |
| [0003](0003-rich-notifications-and-durable-synchronization.md) | Accepted for reference design | Rich notifications feed a durable, idempotent, repairable projection |
| [0004](0004-managed-azure-services-and-regional-recovery.md) | Accepted for reference design | Managed Azure services with separately validated connection and recovery capacity |
| [0005](0005-graph-budgets-and-explicit-write-outcomes.md) | Accepted for reference design | Shared Graph request budgets and explicit uncertain-write handling |
| [0006](0006-slo-driven-capacity-and-feasibility-validation.md) | Accepted for reference design | Separate SLO, Azure-capacity, and real Graph feasibility evidence |
| [0007](0007-message-content-processing-and-storage-boundary.md) | Proposed | Select and approve the message-content processing/storage boundary before enabling the real-data path |
| [0008](0008-simulator-first-single-region-development.md) | Runtime superseded; historical evidence retained | Sweden/private-network decisions retained; simulator, persona login and fixture workloads removed |
| [0009](0009-live-projection-runtime-boundaries.md) | Selected for local implementation; production acceptance conditional | Independent live roles, separated authority/control/projection containers, Strong or single-region SessionPrimary read contracts and disabled-by-default live gates |
| [0010](0010-basic-delegated-teams-validation.md) | Runtime superseded; scope/consent retained | Earlier real delegated validation preserved as history; Development now uses Live only |

## Maintenance

Keep records stable once accepted. A material change in data ownership, permission scope, delivery semantics, compute/connection model, or acceptance criteria requires a new ADR that supersedes or amends the earlier decision.

Revalidate mutable service limits, permissions, SKUs, and regional quotas during implementation. Record real workload evidence and deployment-specific approvals separately from synthetic reference assumptions.
