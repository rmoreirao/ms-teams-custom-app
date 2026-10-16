# ADR 0008: Simulator-first, single-region dev delivery

| Field | Value |
| --- | --- |
| Status | Accepted for dev scope; deployed in Sweden Central with sign-in consent provisioned; authenticated validation pending fresh sign-in |
| Scope | Synthetic content and dedicated test identities only |
| Related decisions | ADRs 0001-0007 |

## Decision

Implement one Sweden Central dev deployment with .NET 10 and React/TypeScript, using the Split View prototype. The user selected Sweden on 2026-10-16 after West Europe/Germany capacity attempts. Keep Front Door Premium/WAF and private app/data services; permit an authenticated public ACR endpoint for GitHub-hosted builds. Anonymous image pull and registry admin credentials are disabled. No deployment VM or self-hosted runner is needed.

Use a stateful simulator for the approved synthetic dev delivery, not an automatic fallback from Teams. Entra identities, managed Azure services, private data access, fixture seeding and public readiness are now provisioned and partly exercised. A completed authenticated browser journey remains pending mandatory Entra security enrollment; live Teams readiness is separate even though the new tenant has licence products available.

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
- Historical attempts used North Europe Cosmos in the original subscription and failed Germany Redis B0/B3/B5 preflights in the new one. The actual user-selected deployment is entirely Sweden Central for regional compute and data, with Redis B0. No regional recovery tier is added. Key Vault retains disabled public access and the trusted Azure-services provisioning exception alongside RBAC.
- Platform bootstrap is independent from test-user creation: resource provisioning can use valid tenant-owned web/callback application IDs, while application/seed deployment retains its four-approved-actor requirement. An incomplete Entra inventory cannot enable authenticated traffic or be treated as a validation pass.
- Container Apps requires the registered network feature discovered during rollout. The initial empty environment failed before that prerequisite was met; the working `env-ready` environment uses a separate delegated subnet, preserving data services. The original subnet is retained during Azure cleanup. Workload forwarded-header trust follows the actual active subnet.

## Live integration boundary

The live delegated-send HTTP transport has isolated contract tests, but is not selected by runtime configuration. Startup rejects non-simulator mode; Graph callbacks return an explicit disabled response.

Licence assignment alone is insufficient to enable live mode. Resource onboarding, endpoint permissions/RSC installation, current Graph entitlement/policy authority, rich notification validation/decryption and subscription lifecycle/repair wiring, protected credential rotation, and real-tenant validation remain work for the live-readiness stage. Do not interpret the disabled callback route as an implemented rich-notification receiver.

## Consequences and acceptance

The dev app can prove UI and application correctness without artificial Teams traffic. Public hosted checks prove only their stated boundaries: private-store seeding, dependency readiness, frontend rendering and authentication controls. Local memory tests and hosted public smoke do not prove completed Entra sessions, all Azure send transactions, Web PubSub user delivery, broker recovery or physical retention.

Record actual cloud/identity approvals, what-if, cost/quota checks, deployment outputs, fixture-job verification, and authenticated browser evidence before declaring the Azure dev rollout complete. Git commits/pushes require separate consent. The production reference ADRs and G0-G7 remain binding for a future real-data deployment.
