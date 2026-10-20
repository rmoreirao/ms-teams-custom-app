# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

People using a work-messaging application to find conversations, read recent context, and send chat messages or channel replies.

## Product Purpose

Expose authorized Microsoft Teams chats and channels in a responsive, focused web application. Teams remains the system of record in the proposed production architecture.

## Operating Context

The repository contains a reference architecture, a bounded Azure Live deployment in Sweden Central, historical validation evidence, and standalone fictional UI prototypes. The executable frontend follows Split View with the New chat search/destination flow. Development is the single environment; normal local and hosted runs use real Entra/Azure/Teams. Simulator and legacy GraphPilot runtimes have been removed; offline fakes exist only in automated tests. The bounded Live profile uses three users, a dedicated group chat/channel and their three approved pairs, with five logical event-processing roles in the existing two apps. Existing notification-to-browser and delegated-send evidence is historical deployment evidence, not proof that every new image has been released. Production policy, scale and recovery acceptance remain gated.

## Capabilities and Constraints

- Dev scope: browse authorized chats/channels, read bounded recent pages, send messages, reply in threads, and safely track delivery uncertainty. Live uses Web PubSub and targeted revision catch-up; there is no selectable simulator or polling transport.
- Split View is selected for dev; the four portable prototypes remain available for comparison.
- New chat discovers approved existing one-to-one mappings under current source authority for both people, without directory grants or Graph creation. The Development profile retains Alex, Sam and Jordan and their three prepared pairs. Selection sends no message. Organization-wide directory access, arbitrary recipients and group-chat creation remain excluded; existing grants are not expanded or removed by runtime cleanup.
- Automatic production chat creation/onboarding remains gated on a separate approved authority/scope contract. Push-mode clients preserve loaded history and reconcile missed hints without the retired 30-second pilot poll.
- Live storage supports the original Strong profile or an explicit single-region SessionPrimary profile with read-only primary-replica document lookups and SDK session barriers. Serverless/Session is not itself a real-time blocker; source contracts and real-service/capacity acceptance remain distinct.
- The bounded Live adapter uses protected original delegated sessions for at most eight hours; source accounts must sign in again after expiry. This is not an unattended production credential or a replacement for organizational policy/DLP authority.
- Each prototype is plain HTML, CSS, and JavaScript, opens directly from disk, and needs no backend, framework, installation, or build.
- Prototype state resets on refresh. Application browser drafts stay in memory; all normal application runs use Cosmos persistence and the approved retention window.
- Production delivery must distinguish pending, confirmed, rejected, and unknown outcomes. The demos illustrate these distinctions without asserting real Graph confirmation.
- Supported Teams rich formatting is sanitized through a maintained allowlist; plain-text sends stay literal. Attachments/embedded media remain in Teams with explicit notices and validated source links. Large loaded histories use measured virtualization. Arbitrary cards and attachment transfer are not implemented.
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
