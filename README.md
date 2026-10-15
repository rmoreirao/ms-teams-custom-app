# Teams-backed chat reference implementation guide

A technical design for a responsive web application that exposes Microsoft Teams chat and channel experiences through Microsoft Graph, using managed Azure services.

The reference design separates application capacity from Microsoft Graph capacity. Recent reads and real-time delivery scale in Azure; user-authored writes remain subject to Teams permissions, latency, and throttling limits.

> **Documentation-only reference.** This repository does not yet contain a runnable application or deployable infrastructure. The workload model is synthetic, the SLOs are proposed targets, and capacity has not been demonstrated. This is an independent guide, not a Microsoft capacity guarantee or endorsement.

## Start here

| Document | Purpose |
| --- | --- |
| [Technical design](docs/technical-design.md) | Requirements, workload model, SLIs/SLOs, Graph constraints, Azure architecture, implementation guidance, and validation gates |
| [Architecture decision records](docs/adr/README.md) | Decisions, alternatives, consequences, and deployment acceptance conditions |
| [Vertical architecture overview](docs/diagrams/architecture-overview.svg) | Readable overview of the interactive read and write paths |
| [Editable diagram source](docs/diagrams/architecture-overview.excalidraw) | Excalidraw source for the architecture overview |

## Architecture at a glance

![Vertical architecture overview: browsers connect through Azure Front Door to Container Apps. Reads use an Azure projection; writes use Microsoft Graph and Teams.](docs/diagrams/architecture-overview.svg)

This overview deliberately shows only the interactive paths. The technical design contains separate top-to-bottom diagrams for confirmed writes and notification-driven synchronization.

## Planning envelope

| Dimension | Reference target |
| --- | --- |
| Concurrent signed-in users | 200,000, with a smaller active-reader/sender population |
| Application API capacity | 15,000 sustained RPS at the specified workload mix |
| Application stress test | 30,000 RPS short bursts against a Graph simulator |
| Client connection capacity | 300,000 provisioned connections, subject to payload and fan-out validation |
| Data ownership | Teams is the system of record; Azure holds an authorized, bounded recent-message projection |

These targets are independent of Graph's endpoint-specific ceilings. A workload that fits the Azure envelope can still exceed Graph's write or conversation limits.

## Reference basis

- [Embed Microsoft Teams in your app](https://learn.microsoft.com/en-us/graph/teams-embed-within-own-app)
- [Best practices for working with Microsoft Graph](https://learn.microsoft.com/en-us/graph/best-practices-concept)
- [Microsoft Graph service-specific throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits)

Service limits and SKU capabilities change. Verify the cited documentation, tenant permissions, regional availability, and subscription quotas before implementing or sizing a deployment.
