# Larkzee Chat UI Design System

## Product context

Larkzee Chat is a lightweight Windows LAN peer-to-peer chat utility. The main window must feel like a focused native desktop tool, not a website, dashboard, enterprise console, or AI-styled concept. Users need only connection state, configuration/connection actions, a readable message stream, and a dependable composer.

Target surface: Windows 10/11 desktop, default client area about 720 x 620, minimum 560 x 450. Preserve all existing product wording and behavior. Do not introduce contacts, avatars, usernames, technical connection details, navigation, history, or unsupported features.

## Visual direction

- Calm Windows-native light appearance with restrained Windows 11 influence.
- Clean hierarchy, generous but efficient whitespace, crisp borders, rounded message bubbles, and almost no shadow.
- Avoid gradients, glass effects, large cards, decorative illustrations, oversized headings, dark themes, and web-dashboard patterns.
- The interface should remain credible when rendered by standard WinForms controls.

## Color tokens

- Window surface: `#FFFFFF`
- Conversation background: `#F7F8FA`
- Primary text: `#182230`
- Secondary text/time: `#7A8492`
- Border/divider: `#DCE1E8`
- Hover/quiet surface: `#F1F4F8`
- Outgoing bubble: `#DCEEFF`
- Outgoing bubble border: `#C7E1FA`
- Incoming bubble: `#FFFFFF`
- Incoming bubble border: `#E1E5EA`
- Primary action: `#1877D2`
- Primary action hover: `#1268BA`
- Primary action text: `#FFFFFF`
- Connected status: `#17864B`
- Disconnected status: `#7A8492`
- Destructive/disconnect text: `#A33A3A` only when needed; never fill a large red button.

## Typography

- Font family: `Segoe UI` only.
- Window title/brand: 15 pt, Semibold.
- Body and message text: 10 pt, Regular.
- Button text: 9 pt, Semibold where supported.
- Timestamp/status: 8-9 pt, Regular.
- Use compact native line height; add spacing around text instead of oversized fonts.

## Spacing and geometry

- Base spacing unit: 4 px.
- Header: 68-72 px high, 20 px horizontal padding, subtle 1 px bottom divider.
- Conversation viewport: 14-16 px horizontal padding and 10-12 px vertical padding.
- Message row gap: 8-10 px.
- Bubble padding: 14 px horizontal, 10 px top, 8-10 px bottom.
- Bubble maximum width: about 66% of the available conversation width, with a practical desktop cap near 460 px at the default window size.
- Bubble radius: 10-12 px; use a slightly tighter corner on the speaker-facing side only if implementation remains simple.
- Composer: 94-104 px high, 12-14 px outer padding, 1 px top divider.
- Composer input: rounded 8-10 px border, no permanently visible scrollbar, comfortable internal padding.
- Send button: 80-88 px wide and 44-52 px high, vertically centered rather than stretching the full composer height.
- Header actions: 82-90 px wide, 32-34 px high, 8-10 px gap.

## Main window structure

1. Header: compact 52px connection toolbar with no product-title block. Left-aligned status pill showing only `● 已连接` or `● 未连接`; right-aligned quiet `⚙ 配置` action (72x28) and connection action (78x28) separated by a 6px gap.
2. Conversation: single continuous light surface. Incoming messages align left; outgoing messages align right. Do not show sender labels or avatars. Long text wraps naturally and must never touch window edges or overlap the composer.
3. Composer: separated from the conversation by a subtle divider. Multiline input occupies most width; the send button is a clear primary action. Enter sends and Shift+Enter inserts a newline.

## Message behavior and states

- Preserve message ordering and automatic scroll-to-latest behavior.
- Timestamps sit inside the bubble below the message with a small gap and muted color.
- Very short bubbles remain compact; long bubbles wrap at the defined maximum width.
- Disabled controls use native accessible disabled treatment and retain readable contrast.
- Do not add persistent message-history language because v1.0 does not save chat history.

## Motion and interaction

- No decorative animation.
- Use native focus cues and predictable hover/pressed states.
- UI must remain responsive while networking runs asynchronously.

## Implementation constraints

- Renderable using .NET 8 WinForms and built-in drawing APIs only.
- No third-party UI libraries, icon packages, fonts, or image assets.
- Preserve accessibility names and keyboard behavior.
- Preserve the exact networking, security, persistence, and error-message contracts.
