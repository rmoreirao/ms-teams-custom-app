# ADR 0006: SLO-driven capacity and feasibility validation

| Field | Value |
| --- | --- |
| Status | Accepted for reference design; production acceptance conditional |
| Date | 2026-10-15 |
| Related requirements | NFR-01, NFR-02, NFR-03, NFR-06, NFR-07, NFR-08 |

## Context

Connected-user count does not specify active interactions, sends, Graph demand, or recipient deliveries. Nominal SKU capacity and a successful simulator benchmark do not prove real Graph feasibility or browser performance.

The reference workload is synthetic. Proposed write/freshness targets depend on Graph behavior for which the embedding reference provides no 200,000-user capacity guarantee.

## Decision

Treat the technical design's workload, SLOs, and engineering envelope as hypotheses requiring evidence.

Maintain separate Azure-capacity, real Graph feasibility, browser/mobile, security/correctness, and recovery tracks. Use Azure Load Testing with JMeter or Locust where appropriate and verify generator capacity.

Measure complete eligible business operations, including retries, budget waits, failures, and uncertain outcomes. Track read availability, content freshness, confirmed sends, and client display separately.

Use isolated, stateful Graph simulators for the high-rate application capacity/stress track. Use approved licensed users and real delegated calls for controlled dependency feasibility tests. Never direct the 30,000-RPS simulator stress envelope at an unapproved tenant or Graph.

Require documented gate evidence before production acceptance. Review synthetic inputs against actual interaction rates, send mix, concentration, ingestion volume, payloads, regions, and device/network profiles.

## Alternatives considered

| Alternative | Reason not selected |
| --- | --- |
| Size from registered/weekly users alone | Does not determine request or delivery demand |
| Rely on published connection maximums | Does not establish API, fan-out, bandwidth, or recovery capacity |
| Treat simulator results as Graph proof | Excludes real endpoint limits, permissions, notification timing, and retries |
| Load-test only real Graph at the application envelope | Can exceed dependency limits and affect other workloads |
| Measure successful HTTP responses only | Hides failures, placeholders, stale content, queueing, and uncertain sends |
| Promise SLOs before a representative test | Turns unverified assumptions into unsupported commitments |

## Consequences

Capacity claims remain bounded and traceable. Separate evidence identifies whether a bottleneck is in the application, Graph, authentication, client, or load generator.

Validation has additional test-environment, licensed-user, coordination, and cost requirements. Some reference targets may need revision or a superseding architecture decision.

Security correctness cannot be traded against an availability error budget.

## Deployment acceptance conditions

- Approve workload definitions, SLO populations, traffic budgets, cost ceilings, and stop criteria.
- Demonstrate the sustained application envelope, connection multiplier, fan-out, and controlled overload/recovery.
- Demonstrate actual Graph demand and send/freshness results within approved dependency budgets.
- Demonstrate browser rendering, revocation, deletion, replay, and regional recovery.
- Publish evidence for gates G1-G7 and record unresolved or accepted deployment risks.

## Links

- [Technical design: workload](../technical-design.md#3-workload-and-capacity-model)
- [Technical design: SLIs/SLOs](../technical-design.md#4-slis-and-proposed-slos)
- [Technical design: validation gates](../technical-design.md#113-acceptance-gates)
- [ADR 0005: Graph budgets](0005-graph-budgets-and-explicit-write-outcomes.md)
- [Azure Load Testing](https://learn.microsoft.com/en-us/azure/app-testing/load-testing/overview-what-is-azure-load-testing)
