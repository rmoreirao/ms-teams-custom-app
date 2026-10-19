# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

People using a work-messaging application to find conversations, read recent context, and send chat messages or channel replies.

## Product Purpose

Expose authorized Microsoft Teams chats and channels in a responsive, focused web application. Teams remains the system of record in the proposed production architecture.

## Operating Context

The repository contains a reference architecture, an explicit basic real-Teams GraphPilot deployment in Sweden Central, and standalone UI prototypes. The executable frontend follows Split View with the New chat search/destination flow. Local simulator hosting uses explicit test credentials and volatile memory. The hosted basic mode uses real Entra/delegated Graph with three users and dedicated synthetic Teams resources; each user passed chat/channel/post/reply read-back journeys. Source UI changes are not automatically deployed. The stronger event-driven production mode remains gated.

## Capabilities and Constraints

- Dev scope: browse authorized chats/channels, read bounded recent pages, send messages, reply in threads, and safely track delivery uncertainty. Local simulator mode is distinct from hosted real-Teams GraphPilot, which refreshes every 30 seconds.
- Split View is selected for dev; the four portable prototypes remain available for comparison.
- New chat searches existing authorized chat titles and opens the existing composer without sending or creating a conversation. Directory/email lookup, person profiles and arbitrary new-chat creation are not enabled. GraphPilot offers only the dedicated approved test chat; it does not imply one-to-one recipients.
- Each prototype is plain HTML, CSS, and JavaScript, opens directly from disk, and needs no backend, framework, installation, or build.
- Prototype state resets on refresh. Dev browser drafts stay in memory; local server data lasts until process restart, while Azure configuration uses Cosmos persistence and an explicit retention window.
- Production delivery must distinguish pending, confirmed, rejected, and unknown outcomes. The demos illustrate these distinctions without asserting real Graph confirmation.
- Calling, meetings, presence, typing indicators, attachments, and a durable offline-send queue are outside prototype scope.

## Evidence on Hand

- `README.md`: repository entry point and reference-design limitations.
- `docs\technical-design.md`: requirements and interaction semantics.
- `docs\detailed-architecture.md`: proposed component and data contracts.
- `docs\adr\0005-graph-budgets-and-explicit-write-outcomes.md`: explicit delivery states and safe handling of uncertainty.
- `src\`, `infra\`, and `.github\workflows\`: dev implementation and deployment definitions; remote GitHub workflow execution and production capacity are not demonstrated.
- `docs\dev-validation.md`: correctness evidence and outstanding approval/live-integration gates.

## Product Principles

- Make finding, reading, and replying to a conversation straightforward.
- Keep secondary controls out of the primary task.
- Explain delivery uncertainty rather than silently resending.
- Clearly distinguish synthetic demonstrations from production capabilities.

## Accessibility & Inclusion

The Split View implementation and prototypes support desktop/mobile browsers, keyboard navigation, labeled controls, visible focus, reduced motion, and light/dark themes.
