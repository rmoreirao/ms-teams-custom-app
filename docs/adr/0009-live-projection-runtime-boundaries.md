# ADR 0009: Live projection runtime and consistency boundaries

| Field | Value |
| --- | --- |
| Status | Selected for local implementation; production acceptance conditional |
| Date | 2026-10-19 |
| Scope | Live runtime foundation; does not change the hosted synthetic dev deployment |
| Related decisions | ADRs 0001-0008 |

## Decision

Retain one codebase and image with independent Api, Receiver, Projector, Delivery and Control roles. Separate projection, authority and subscription/repair state into `live-state`, `live-authority` and `live-control` containers. Keep the simulator and its aggregate schema isolated; do not migrate fixture bodies into live data.

Support two explicit Cosmos read profiles. The original `Strong` profile remains the default and requests account-level Strong reads. `SessionPrimary` supports a single-readable/single-writable-region Session account, including serverless, without upgrading its consistency setting. It executes the bounded, read-only `readCurrentV1` stored procedure for point reads in projection, authority and control containers. Microsoft documents that stored procedures execute on the primary replica and their reads offer strong consistency; the SDK captures the returned partition session token for subsequent same-client queries. Point reads therefore establish a current revision/authority barrier instead of assuming independent Session clients share tokens.

The procedure returns an explicit versioned document/ETag or primary-confirmed absence. Missing procedure/execute permission, rejected execution, invalid identity/schema or missing session evidence fails explicitly without falling back to an ordinary Session point read. Partition-local conditional transactions still enforce operation and writer epochs. No opaque session-token parsing, shared-token cache or preview read-consistency SDK feature is used.

Use role-scoped container access, server-held delegated web tokens, separately federated sync credentials, rich encrypted notifications, partition-local projection/change/outbox transactions, body-free delivery hints and bounded repair. A deployment-provided authoritative policy feed is mandatory; the app does not infer production authority from fixture data or broad Graph application roles.

For the unattended implementation, retain the explicit organizational policy-feed integration rather than treating Graph membership alone as complete history/DLP authority. The repository implements the authenticated client, evidence contract, freshness/epoch checks and revocation enforcement. Supplying the approved service and its policy semantics is a deployment prerequisite, just as supplying the tenant, keys and data platform is; an absent or failed service never grants access. No built-in Graph-only policy shortcut is selected or implied.

Synchronization and user admission are independent, disabled-by-default gates. A positive configured writer epoch is checked against persisted state, and operation claims conditionally fence the conversation epoch. These mechanisms do not constitute cross-region writer election or permission to replay uncertain writes.

Outbox dispatch uses Cosmos change feed with a separate durable lease container, then a dedicated duplicate-safe delivery queue and conditional fan-out page checkpoints. Completion and the published-revision watermark are partition-local. Repair remains bounded scheduled work. Real change-feed/broker recovery, fan-out capacity and regional owner election/restore remain production acceptance evidence, not features inferred from independently scalable host roles.

## Consequences and acceptance

Strong consistency and SessionPrimary are implementation profiles, not prerequisites for pub/sub itself. SessionPrimary removes the need to replace or upgrade the current account merely for this correctness boundary. Runtime metadata checks and deployment preflight reject unsupported consistency/topology combinations; neither changes account settings automatically. Multi-region Session behavior is not accepted by this profile.

SessionPrimary adds primary stored-procedure RU/latency and primary-replica load. Its correctness mechanism does not establish the reference capacity envelope; measure actual point-read/query and fan-out costs before sizing. The custom authority/control reader adds stored-procedure execution but no item-write permission. Only read-only procedures may be deployed in containers reachable by those readers, because Cosmos grants execution at container scope rather than per procedure. Script changes remain part of the reviewed deployment trust boundary.

Canonical procedure behavior, result validation, runtime guards, deployment checks and compiled Bicep are locally verified. An opt-in two-SDK-client synthetic Cosmos test separately exercises projection reads, revocation and epoch fencing; it is explicitly skipped unless its target/authorization inputs are supplied. Local tests do not certify actual service replication, capacity or regional recovery.

The reference multi-region targets and G0-G7 still apply. A different consistency/recovery profile needs explicit design and evidence. Deploying local code, compiling Bicep, granting Graph permissions or setting an approval-reference string cannot satisfy those gates.

See [live runtime and rollout](../live-runtime.md) for the implemented configuration, data handling, staging sequence and remaining blockers.

References: [stored-procedure data consistency](https://learn.microsoft.com/en-us/azure/cosmos-db/stored-procedures-triggers-udfs#data-consistency), [session tokens](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency#utilize-session-tokens), [native data-plane actions](https://learn.microsoft.com/en-us/azure/cosmos-db/reference-data-plane-security#built-in-actions).
