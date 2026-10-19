# ADR 0009: Live projection runtime and consistency boundaries

| Field | Value |
| --- | --- |
| Status | Selected for local implementation; production acceptance conditional |
| Date | 2026-10-19 |
| Scope | Live runtime foundation; does not change the hosted synthetic dev deployment |
| Related decisions | ADRs 0001-0008 |

## Decision

Retain one codebase and image with independent Api, Receiver, Projector, Delivery and Control roles. Separate projection, authority and subscription/repair state into `live-state`, `live-authority` and `live-control` containers. Keep the simulator and its aggregate schema isolated; do not migrate fixture bodies into live data.

Use a Strong-consistency-capable Cosmos account for this implementation profile. The live client explicitly requests Strong consistency and deployment preflight refuses a weaker account. This is a deliberate, simple correctness boundary for cross-client authorization and revision reads until a separately verified session-token/read-barrier alternative is implemented.

Use role-scoped container access, server-held delegated web tokens, separately federated sync credentials, rich encrypted notifications, partition-local projection/change/outbox transactions, body-free delivery hints and bounded repair. A deployment-provided authoritative policy feed is mandatory; the app does not infer production authority from fixture data or broad Graph application roles.

For the unattended implementation, retain the explicit organizational policy-feed integration rather than treating Graph membership alone as complete history/DLP authority. The repository implements the authenticated client, evidence contract, freshness/epoch checks and revocation enforcement. Supplying the approved service and its policy semantics is a deployment prerequisite, just as supplying the tenant, keys and data platform is; an absent or failed service never grants access. No built-in Graph-only policy shortcut is selected or implied.

Synchronization and user admission are independent, disabled-by-default gates. A positive configured writer epoch is checked against persisted state, and operation claims conditionally fence the conversation epoch. These mechanisms do not constitute cross-region writer election or permission to replay uncertain writes.

Outbox dispatch uses Cosmos change feed with a separate durable lease container, then a dedicated duplicate-safe delivery queue and conditional fan-out page checkpoints. Completion and the published-revision watermark are partition-local. Repair remains bounded scheduled work. Real change-feed/broker recovery, fan-out capacity and regional owner election/restore remain production acceptance evidence, not features inferred from independently scalable host roles.

## Consequences and acceptance

Strong consistency makes the chosen cross-client correctness requirement explicit instead of asserting that independent Session-consistency clients share a read barrier. It affects cost, latency, availability and regional topology; measure them before production sizing. The dev account is not silently upgraded or reused.

The reference multi-region targets and G0-G7 still apply. A different consistency/recovery profile needs explicit design and evidence. Deploying local code, compiling Bicep, granting Graph permissions or setting an approval-reference string cannot satisfy those gates.

See [live runtime and rollout](../live-runtime.md) for the implemented configuration, data handling, staging sequence and remaining blockers.
