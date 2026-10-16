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

## Split View outreach extensions

Three additional prototypes extend the selected dev implementation's Split View, without changing the app or the original four layouts. Their task mode is **Operate**: find the right colleague, confirm their identity, and start a one-to-one conversation. Group-chat creation is intentionally excluded.

| File | Entry point | Discovery and confirmation | Trade-off |
| --- | --- | --- | --- |
| `05-new-chat.html` | **New chat** beside the conversation-list title | Name/email search, suggested recipients, identity in the composer header | Fastest when the colleague's name is known; less useful for role-based discovery |
| `06-people-directory.html` | **People directory** below the conversation filters | Name/email/role/department search, department filter, profile side pane | Better for finding unfamiliar colleagues; adds a directory surface |
| `07-contextual-profile.html` | A channel author or named participant | Profile side pane with role, email, office, and shared channel | Preserves the reason for reaching out; requires a shared discussion or participant list |

All three inherit the existing theme, typography, navigation, channel threads, drafts, and simulated-delivery controls. They share a fictional eight-person directory, including two Jordan Lees to demonstrate identity disambiguation and one account with messaging unavailable. Directory/profile details are illustrative, not claims about a live organization.

Discovery replaces the reading pane, not the conversation list. A profile is a nonmodal third pane on wide screens, replaces the reading pane on intermediate screens, and becomes a full task surface on mobile. Escape closes a profile before closing discovery, restoring focus to its source. Named author controls are keyboard buttons; search results remain normal tab-navigable controls rather than an incomplete custom combobox.

Opening a person reuses their existing direct chat. A new recipient gets an in-memory composer; their chat joins recent conversations on the first send. Switching recipients preserves independent drafts, and abandoning an unsent composer does not create a recent-chat row. Profile and composer headers expose role/email before sending. No message is sent by selecting a person or choosing **Message**.

Everything remains synthetic and local to each open page. No directory lookup, Graph write, authentication, presence, or real Teams delivery is implemented.

Run `npm run test:prototypes` from `src\Web` for the outreach journeys in desktop/mobile and light/dark modes. It uses the existing Playwright dependency and a temporary Python static server; it does not start the app, load credentials, or call Microsoft services.
