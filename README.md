# Teams-backed chat reference implementation guide

A technical design for a responsive web application that exposes Microsoft Teams chat and channel experiences through Microsoft Graph, using managed Azure services.

The reference design separates application capacity from Microsoft Graph capacity. Recent reads and real-time delivery scale in Azure; user-authored writes remain subject to Teams permissions, latency, and throttling limits.

> **Basic real-Teams validation deployed in Sweden Central.** [Open the Web App](https://teamsrefdev-unc3jasrpkgbo-web-htdnfyf3dbbjbca2.b02.azurefd.net). Alex, Sam and Jordan completed real chat read/send, channel read/post and thread reply journeys through delegated Graph. This explicit `GraphPilot` mode reuses existing infrastructure and refreshes every 30 seconds; the stronger event-driven production mode remains gated. SLOs are proposed targets, not demonstrated production capacity.

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
| [Basic real-Teams validation](docs/graph-pilot.md) | Deployed three-user scope, actual Graph read/write evidence, API-only releases, private configuration and MFA campaign handling |
| [Live Teams implementation plan](docs/live-integration-plan.md) | Dependency-ordered work packages for the final event-driven projection, live Graph integration, scaling, and recovery |
| [Live runtime and rollout](docs/live-runtime.md) | Implemented roles/configuration, policy-feed contract, staged deployment, correctness boundaries, and explicit production blockers |
| [Entra configuration and rebuild](docs/entra-configuration.md) | Identity desired-state reconciliation, reusable rebuild sequence, federation/consent distinctions, and remaining manual approval/rotation steps |
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

### New chat in the application

The executable frontend now follows [the New chat reference](prototypes/05-new-chat.html): **New chat** beside Messages opens a searchable destination pane, and choosing a chat opens its existing composer with the draft intact. Keyboard focus follows the task; Escape closes search, and narrow screens show one task at a time. Selecting a destination sends nothing and creates no duplicate conversation.

This is capability-aware, not an organization directory. Search uses authorized, loaded chat titles; pagination can load additional conversations. The backend does not expose directory profiles/email lookup or arbitrary chat creation. The UI explains that limitation instead of inventing people or reporting a fake new chat. In GraphPilot only the dedicated approved test chat is offered, including its existing group-chat semantics. Message delivery, channel replies, authorization, 30-second pilot refresh and in-memory-only drafts retain their existing contracts.

### Reach someone new: three standalone Split View prototypes

These standalone prototypes explore **one-to-one outreach**, including fictional capabilities not enabled in the app. Open them directly from disk or use [the prototype comparison](prototypes/index.html).

| Prototype | How to reach a colleague |
| --- | --- |
| [New chat](prototypes/05-new-chat.html) | Search a known name or email, verify the recipient, then write |
| [People directory](prototypes/06-people-directory.html) | Find someone by role or department, inspect their profile, then message |
| [Message from a profile](prototypes/07-contextual-profile.html) | Select a channel author or participant and start a direct conversation from their profile |

Each flow reuses an existing direct chat, preserves per-person drafts, and adds a new chat to recents on its first demo send. Try Priya Nair for a new conversation, Maya Chen for an existing one, or the two Jordan Lees to compare role/email disambiguation. All profiles and messages are fictional; nothing reaches your organization or Teams. Group-chat creation is outside this exploration.

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
