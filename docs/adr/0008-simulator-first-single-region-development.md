# ADR 0008: Simulator-first, single-region dev delivery

| Field | Value |
| --- | --- |
| Status | Accepted for dev scope; cloud/identity execution approval is outstanding |
| Scope | Synthetic content and dedicated test identities only |
| Related decisions | ADRs 0001-0007 |

## Decision

Implement one West Europe dev deployment with .NET 10 and React/TypeScript, using the Split View prototype. Keep Front Door Premium/WAF and private app/data services; permit an authenticated public ACR endpoint for GitHub-hosted builds. Anonymous image pull and registry admin credentials are disabled. No deployment VM or self-hosted runner is needed.

Use a stateful simulator instead of Teams while licences are unavailable. Entra sign-in and managed Azure infrastructure are intended real integrations, but have not yet been provisioned or exercised. Never silently fall back from Graph to the simulator.

The approved data boundary permits newly generated synthetic content, a seven-day recent-message window, and documented supporting copies. It does not authorize reading existing business content, purchasing licences, broad Graph consent, or production processing.

Regional recovery and load testing are explicitly excluded. Do not infer capacity acceptance from unit/browser tests or provisioned SKU limits.

## Dev implementation choices

- API/BFF and worker are separate deployments of one image, plus an in-network seed/maintenance job. The worker combines projection, body-free hint delivery, and bounded maintenance; this is not the five independent production scaling/credential boundaries in the target architecture.
- Cosmos holds a conversation aggregate, operation journal, simulator receipts, and principal revocation records. Confirmation and projection changes use one-partition conditional transactions. Aggregates retain at most 200 recent messages, clipped further to a serialized message budget of 1 MB; there are at most 100 fixture conversations. This is a dev model, not a production hot-partition solution or an unbounded seven-day archive.
- Redis holds protected BFF sessions/token cache/key-ring material and shared command permits. A separate Redis hot-message-view cache is not implemented. Normal dev reads use Cosmos.
- The aggregate revision/change list is the durable dev outbox. A worker publishes at least once, then conditionally advances its watermark; clients refetch through authorization. Stable revisions and refetches tolerate duplicate hints.
- Synthetic membership/history and durable application offboarding are authoritative only for fixture scenarios. They do not demonstrate Graph/DLP/offboarding-source completeness or a production revocation bound.
- Source tombstones survive message eviction and are pruned after fourteen days. Reaching the dev tombstone safety bound makes coverage unavailable rather than serving a potentially resurrected view.
- Unknown simulator results resolve using an exact receipt stored in the state store, including after a simulator object restart. The live send transport intentionally has no body/time-heuristic resolution or automatic POST retry.
- Azure uses one API replica and one worker replica. Simulator next-outcome controls are process-local and are not a distributed fault-injection facility.
- Data/origin access is private, but outbound connectivity uses the Container Apps platform path; a dedicated NAT/firewall policy is not implemented or claimed.
- Deployment capacity exception: Cosmos regional access is denied for this subscription in West Europe, so the synthetic dev Cosmos account uses North Europe through a West Europe private endpoint. Compute remains in West Europe; Redis uses B1 because B0 is unavailable there. No recovery region or replicated serving tier is added. Key Vault retains disabled public access and enables only the trusted Azure-services provisioning exception alongside RBAC.

## Live integration boundary

The live delegated-send HTTP transport has isolated contract tests, but is not selected by runtime configuration. Startup rejects non-simulator mode; Graph callbacks return an explicit disabled response.

Licence assignment alone is insufficient to enable live mode. Resource onboarding, endpoint permissions/RSC installation, current Graph entitlement/policy authority, rich notification validation/decryption and subscription lifecycle/repair wiring, protected credential rotation, and real-tenant validation remain work for the live-readiness stage. Do not interpret the disabled callback route as an implemented rich-notification receiver.

## Consequences and acceptance

The dev app can prove UI and application correctness without licences or artificial Teams traffic. Local memory tests do not prove Azure transactions, networking, managed identity, Web PubSub admission, broker redelivery, or cloud retention.

Record actual cloud/identity approvals, what-if, cost/quota checks, deployment outputs, fixture-job verification, and authenticated browser evidence before declaring the Azure dev rollout complete. Git commits/pushes require separate consent. The production reference ADRs and G0-G7 remain binding for a future real-data deployment.
