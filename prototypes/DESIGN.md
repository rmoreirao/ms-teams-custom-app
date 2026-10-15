# Messaging prototype collection

## Status and scope

Four equally weighted UI alternatives explore the same operational task: find a chat or channel, read context, send a message, and reply to a channel post. None is the selected production design. These rules apply only to `prototypes`, not to architecture diagrams or a future production frontend.

## Visual language

- Use the embedded Clawpilot theme variables. Light mode uses warm neutral surroundings and white reading surfaces; dark mode uses charcoal surfaces.
- Use the rose accent only for primary actions, selected conversations/filters, replies, and focus.
- On dark surfaces, secondary text uses the brighter `--cp-text-soft` neutral through a `--cp-text-muted` override to retain readable contrast on elevated and selected surfaces.
- Use the Segoe UI/Aptos/Calibri sans-serif stack, a compact type hierarchy, and a roughly 72-character maximum measure for messages.
- Separate regions with subtle borders. Buttons use 10px rounding; conversation containers and post surfaces use 16px rounding. Avoid decorative shadows, gradients, imagery, and entrance animation.
- Keep the composer, conversation title, and clear back/close actions distinct from message content.

## Layout alternatives

| File | Navigation | Channel replies | Narrow screens |
| --- | --- | --- | --- |
| `01-split-view.html` | Persistent recent-conversation list with search and type filters | Contextual thread pane | List, conversation, and thread become separate task surfaces |
| `02-focus.html` | Temporary, keyboard-contained conversation switcher | Thread temporarily replaces the main feed | Same single-conversation model; switcher is dismissible |
| `03-inbox.html` | Full-width conversation overview; compact list remains beside an open reader on desktop | Inline expansion inside the reading panel | Reader replaces the list until closed |
| `04-workspace.html` | Chats plus expandable team/channel groups | Inline replies within channel posts | List and conversation become separate task surfaces |

Newly opened channel feeds begin at the first post; chats open at their most recent messages. Sending scrolls to the new demo message. Do not add unrelated feature chrome to differentiate the alternatives.

## Shared interaction contract

- Use identical fictional people, messages, and conversations for comparison.
- Search conversation names and team names; filters select all, chats, or channels.
- Preserve independent conversation and thread drafts in memory.
- Enter sends, Shift+Enter inserts a newline, and IME composition does not submit.
- Show pending before a simulated result. Label delivery as demo behavior, never real Teams confirmation.
- Definitive rejection allows a deliberate retry. Unknown commitment allows a simulated check of the original send, not an automatic resend.
- Distinguish empty, loading, unavailable, and offline states. Explain recovery actions and never imply an offline-send queue.
- Treat user-entered message bodies as plain text. Wrap long text rather than expanding the page.
- Keep demo scenarios collapsed by default and disclose the lack of a Teams connection persistently.

## Accessibility and delivery

Use semantic landmarks, labeled controls, visible focus, readable contrast, and reduced-motion preferences. Move focus into newly opened conversations/threads and restore it on dismissal. The Focus switcher contains keyboard focus while open; Escape closes it. Escape also closes threads, reading views where appropriate, and demo controls.

Each HTML file contains its own CSS, JavaScript, icons, and fixtures. It opens directly with `file://` and has no network or shared-runtime dependencies. Inline duplication is deliberate so the files remain independently portable. No browser storage or service worker is used; refresh restores fixtures. Theme choice is propagated through local links in the URL.
