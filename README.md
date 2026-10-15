# Teams-backed chat reference implementation guide

A technical design for a responsive web application that exposes Microsoft Teams chat and channel experiences through Microsoft Graph, using managed Azure services.

The reference design separates application capacity from Microsoft Graph capacity. Recent reads and real-time delivery scale in Azure; user-authored writes remain subject to Teams permissions, latency, and throttling limits.

> **Simulator-first dev implementation.** The repository now includes a .NET 10 BFF/API and worker, a React Split View frontend, synthetic fixtures, Bicep, and GitHub Actions definitions. Local correctness/browser checks pass. Azure rollout is blocked: the selected subscription is disabled/read-only, and directory/provisioning approvals are outstanding. Live Teams integration is disabled. SLOs remain proposed targets; production capacity has not been demonstrated.

## Run the dev application

Install .NET 10 SDK, Node.js 22.12 or newer in the Node 22 line, npm, and PowerShell 7. From the repository root:

```powershell
Push-Location src\Web
npm ci
npm run build
Pop-Location
.\scripts\Start-Local.ps1
```

Choose a temporary development password at the prompt, open `http://localhost:5255`, select **Local development sign-in**, and use a fixture persona with that password. No default password is supplied. Local mode uses volatile server memory; Azure mode requires Entra and managed services. Both visibly distinguish simulator confirmation from Teams delivery.

Run `.\scripts\Validate.ps1 -Browser` for build, unit/component, authenticated API, and real-browser checks. Browser prerequisites and the Azure/Entra approval gates are covered in the [dev guide](docs/dev-guide.md).

## Start here

| Document | Purpose |
| --- | --- |
| [Dev setup and deployment](docs/dev-guide.md) | Executable components, private-network Azure bootstrap, Entra prerequisites, approvals, GitHub Actions, and cleanup |
| [Test data and scenarios](docs/testing.md) | Fixture identities, repeatable seeding/reset, browser scenarios, and live Teams prerequisites |
| [Functional validation evidence](docs/dev-validation.md) | Local results and the explicit distinction between validated, blocked, and unexecuted work |
| [Technical design](docs/technical-design.md) | Requirements, workload model, SLIs/SLOs, Graph constraints, Azure architecture, implementation guidance, and validation gates |
| [Detailed architecture](docs/detailed-architecture.md) | Azure/Entra components, software boundaries, data/API contracts, runtime sequence diagrams, and recovery/operations details |
| [Privacy and storage comparison](docs/detailed-architecture.md#56-privacy-and-storage-options) | Thirteen data-handling options, including the proposed Cosmos DB projection, no-copy variants, encryption, and privacy/performance trade-offs |
| [Architecture decision records](docs/adr/README.md) | Decisions, alternatives, consequences, and deployment acceptance conditions |
| [Vertical architecture overview](docs/diagrams/architecture-overview.svg) | Readable overview of the interactive read and write paths |
| [Editable diagram source](docs/diagrams/architecture-overview.excalidraw) | Excalidraw source for the architecture overview |

## Try the UI prototypes

Open [the prototype comparison](prototypes/index.html) locally in a browser, or double-click `prototypes\index.html`. No installation, server, or build step is needed. GitHub displays HTML source; download or clone the repository to run the demos.

| Prototype | Simple interaction model |
| --- | --- |
| [Split view](prototypes/01-split-view.html) | Conversation list beside the message feed, with a separate channel-thread panel |
| [Focus](prototypes/02-focus.html) | One conversation, an on-demand switcher, and single-surface thread navigation |
| [Inbox](prototypes/03-inbox.html) | Conversation summaries first; open a reading panel when needed |
| [Workspace](prototypes/04-workspace.html) | Chats and team/channel navigation beside posts with inline replies |

Every file is self-contained HTML, CSS, and JavaScript with the same fictional sample workspace. Search conversations, switch between chats and channels, send a demo message, or open a channel thread and reply. Enter sends; Shift+Enter adds a newline. Drafts are retained while switching within an open page. Use the theme button for light/dark mode; `?clawpilotTheme=light` or `?clawpilotTheme=dark` also selects a theme.

**Demo controls** expose loading/unavailable views, rejected/uncertain sends, simulated offline/reconnect behavior, and reset. A status check resolves the original uncertain demo send without creating a second message. Offline mode does not queue sends. All state lives in memory and resets on refresh; nothing connects to Teams or persists messages on the device. There is no real authentication, authorization, message synchronization, or Graph confirmation.

The four layouts remain standalone alternatives; **Split View is selected for the executable dev frontend**, not asserted as a production acceptance decision. [Prototype design notes](prototypes/DESIGN.md) describe their common rules and differences.

## Architecture at a glance

![Vertical architecture overview: browsers connect through Azure Front Door to Container Apps. Reads use an Azure projection; writes use Microsoft Graph and Teams.](docs/diagrams/architecture-overview.svg)

This overview deliberately shows only the interactive paths. The technical design contains separate top-to-bottom diagrams for confirmed writes and notification-driven synchronization. The detailed architecture adds focused component views and runtime sequence diagrams.

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
