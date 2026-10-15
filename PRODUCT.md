# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

People using a work-messaging application to find conversations, read recent context, and send chat messages or channel replies.

## Product Purpose

Expose authorized Microsoft Teams chats and channels in a responsive, focused web application. Teams remains the system of record in the proposed production architecture.

## Operating Context

The repository contains a reference architecture and four standalone, clickable UI prototypes. The prototypes use fictional sample data and simulated outcomes; they are not connected to Teams, Microsoft Graph, Entra, or Azure.

## Capabilities and Constraints

- Prototype scope: browse chats and channels, read messages, send demo messages, and reply to channel posts.
- Four alternative layouts explore simplicity without choosing a final production interface.
- Each prototype is plain HTML, CSS, and JavaScript, opens directly from disk, and needs no backend, framework, installation, or build.
- Demo messages and drafts remain in memory and reset on refresh.
- Production delivery must distinguish pending, confirmed, rejected, and unknown outcomes. The demos illustrate these distinctions without asserting real Graph confirmation.
- Calling, meetings, presence, typing indicators, attachments, and a durable offline-send queue are outside prototype scope.

## Evidence on Hand

- `README.md`: repository entry point and reference-design limitations.
- `docs\technical-design.md`: requirements and interaction semantics.
- `docs\detailed-architecture.md`: proposed component and data contracts.
- `docs\adr\0005-graph-budgets-and-explicit-write-outcomes.md`: explicit delivery states and safe handling of uncertainty.
- No implemented production backend, infrastructure, measured capacity, or customer evidence is supplied.

## Product Principles

- Make finding, reading, and replying to a conversation straightforward.
- Keep secondary controls out of the primary task.
- Explain delivery uncertainty rather than silently resending.
- Clearly distinguish synthetic demonstrations from production capabilities.

## Accessibility & Inclusion

The prototypes support desktop and mobile browsers, keyboard navigation, labeled controls, visible focus, reduced motion, and light/dark themes.
