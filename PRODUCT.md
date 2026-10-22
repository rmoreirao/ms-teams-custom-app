# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

People using a work-messaging application to find conversations, read recent context, and send chat messages or channel replies.

## Product Purpose

Expose authorized Microsoft Teams chats and channels in a responsive, focused web application. Teams remains the system of record in the proposed production architecture.

## Operating Context

The repository contains a reference architecture, a bounded Azure Live deployment in Sweden Central, historical validation evidence, and standalone fictional UI prototypes. The executable frontend follows Split View with the New chat search/destination flow. Development is the single environment; normal local and hosted runs use real Entra/Azure/Teams. Simulator and legacy GraphPilot runtimes have been removed; offline fakes exist only in automated tests. The bounded Live profile uses three users, a dedicated group chat/channel and their three approved pairs, with five isolated event-processing workloads. Existing notification-to-browser and delegated-send evidence is historical deployment evidence, not proof of acceptance for every new image. Production policy, scale and recovery acceptance remain gated.

## Capabilities and Constraints

- The explicitly approved service-authenticated Dev migration is deployed: five isolated role workloads, app-only background Graph access and bounded real Graph membership/admission policy. Consent, federation, scoped access and callback routing were applied; all five app-owned subscriptions, resource coverage and expected memberships were verified before admission reopened. The three users remain ordinary interactive users, not background credentials. Renewal, the eight-hour observation and fresh authenticated acceptance are tracked separately. This is not enterprise policy/DLP equivalence.

- Dev scope: browse authorized chats/channels, read bounded recent pages, send messages, reply in threads, and safely track delivery uncertainty. Live uses Web PubSub and targeted revision catch-up; there is no selectable simulator or polling transport.
- Split View is selected for dev; the four portable prototypes remain available for comparison.
- New chat discovers approved existing one-to-one mappings under current source authority for both people, without organization-wide directory discovery or Graph creation. The Development profile retains Alex, Sam and Jordan and their three prepared pairs. Selection sends no message. The staged service policy's reviewed background directory-read permissions are distinct from UI discovery; arbitrary recipients and group-chat creation remain excluded. Runtime cleanup does not implicitly expand or remove existing grants.
- Automatic production chat creation/onboarding remains gated on a separate approved authority/scope contract. Push-mode clients preserve loaded history and reconcile missed hints without the retired 30-second pilot poll.
- Live storage requires explicit Session content reads on the existing single-region account. Critical authorization/control/write decisions retain read-only current reads; browser revisions bound convergence without visible rollback. Legacy profile names require an explicit configuration migration. This repository change has not been deployed; source freshness and real-service/capacity acceptance remain distinct.
- Availability-first source keeps valid authorized history visible during synchronization/operational failures. Cold views use bounded durable Control hydration and typed automatic loading; known-empty is distinct from unknown. Missed edits/deletions can remain until reconciliation or seven-day content expiry. Authority expiry, revocation, history, parent and epoch checks still fail closed. The new queue/scoped grants and rebuilt coordinated image require a separate release; this behavior is not yet deployed.
- Interactive sessions still expire normally after eight hours. Background synchronization uses Control's federated application identity independently; the retained-user legacy source remains only until post-cutover acceptance and cleanup.
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
