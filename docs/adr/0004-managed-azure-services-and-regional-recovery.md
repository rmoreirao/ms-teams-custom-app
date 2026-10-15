# ADR 0004: Managed Azure services and regional recovery

| Field | Value |
| --- | --- |
| Status | Accepted for reference design; production acceptance conditional |
| Date | 2026-10-15 |
| Related requirements | FR-05, NFR-01, NFR-02, NFR-06, NFR-08 |

## Context

Long-lived connections, API execution, projection storage, event processing, and Graph calls have different capacity drivers. Running every connection on API servers unnecessarily couples those drivers.

Managed connection capacity and application autoscaling do not remove quotas, payload/fan-out constraints, hot partitions, or recovery requirements.

## Decision

Use Front Door/WAF, Container Apps, Cosmos DB, Azure Managed Redis, Service Bus Premium, and Web PubSub Premium_P2 as the reference service set.

Offload client connections and fan-out to Web PubSub. Use dedicated workload profiles, supported zone redundancy, measured scale rules, and nonzero warm API capacity. Prefer scheduled pre-warming for predictable peaks.

Plan at least 300,000 provisioned client connections, initially 300 Web PubSub units on connection count alone. Request the required regional subscription quota increase above the default 150-unit quota, accounting for existing usage and measured extra capacity.

Start with one active application/write region and a warm recovery region. Replicate required projection, operation, and entitlement state; explicitly design queue-data recovery. Provision each surviving connection replica/region for the full accepted load.

Fence old writers and restore authoritative operation/authorization state before enabling writes after failover. Keep the Graph budget shared across the deployment, not multiplied by regional replicas.

## Alternatives considered

| Alternative | Reason not selected as the default |
| --- | --- |
| API replicas host all WebSockets | Couples connection count to API lifecycle, scale, and operational complexity |
| AKS/self-managed messaging | Valid when organizational/platform constraints justify it; not necessary for the initial managed pattern |
| Single-region deployment | Does not address the proposed regional recovery objective |
| Active-active application writes immediately | Requires additional fencing, conflict, operation-state, and uncertain-send semantics |
| Scale solely on connection utilization | Does not account for delivery bandwidth, payloads, or server load |
| Rehydrate all state from Graph during failover | Recovery can exceed throttled dependency budgets and the proposed RTO |

## Consequences

API scale is decoupled from client connections, with less infrastructure ownership than a self-managed socket fleet.

Premium services, replicated state, and full failover capacity increase cost. Regional availability and subscription quotas need early approval. Autoscaling is not instantaneous.

An Azure failure does not remove a confirmed Teams message, but it can remove local knowledge of confirmation. Recovery cannot interpret missing state as proof that a POST did not commit.

## Deployment acceptance conditions

- Verify regional/SKU availability, supported unit counts, quota approvals, and networking.
- Demonstrate API, connection, fan-out, bandwidth, cache, and database-partition capacity.
- Demonstrate zone failure, jittered reconnect, writer fencing, queue-data recovery, and full-load regional recovery.
- Validate proposed RTO/RPO against the required state, not only routing health.
- Demonstrate that authorization/rate-control uncertainty does not fail open.

## Links

- [Technical design: Azure architecture](../technical-design.md#6-azure-architecture)
- [Technical design: resilience](../technical-design.md#9-resilience-and-regional-recovery)
- [Web PubSub reliability](https://learn.microsoft.com/en-us/azure/reliability/reliability-web-pubsub)
- [Web PubSub quotas](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#azure-web-pubsub-limits)
- [Container Apps scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app)
