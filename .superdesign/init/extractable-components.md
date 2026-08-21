# Extractable components

## Layout components

### MainAppShell
- Source: `Forms/MainForm.cs` (`MainForm`)
- Category: layout
- Description: Persistent header/feed/composer desktop shell.
- Extractable props: `isConnected`, `isConnecting`, `isClosing`; `onOpenSettings`, `onToggleConnection`.
- Hardcoded: product title, window sizes, docking order, light styling.

### ChatHeader
- Source: `Forms/MainForm.cs` (`BuildHeader`)
- Category: layout
- Description: Product identity, connection state, settings, and connection actions.
- Extractable props: `isConnected`, `isConnecting`; open-settings/toggle actions.
- Hardcoded: title, dot/gear glyphs, Chinese labels, 66 px geometry.

### MessageComposer
- Source: `Forms/MainForm.cs` (`BuildInputArea`)
- Category: layout
- Description: Multiline draft input and send action.
- Extractable props: `enabled`, `isSending`, `draftText`; `onSend`.
- Hardcoded: 112 px height, 8,000-character cap, Enter/Shift+Enter behavior, labels.

## Basic components

### ChatMessageControl
- Source: `Controls/ChatMessageControl.cs`
- Category: basic
- Description: Wrapped timestamped bubble aligned by ownership.
- Extractable props: `message`, `timestamp`, `isOwnMessage`.
- Hardcoded: 74% width cap, spacing, Segoe UI, bubble colors and rectangular shape.

### ConnectionStatus
- Source: `Forms/MainForm.cs` (`BuildHeader`, `ApplyConnectionState`)
- Category: basic
- Description: Binary connected/disconnected indicator.
- Extractable props: `isConnected`.
- Hardcoded: Chinese labels, dot glyph, `#1E8246`/`#6E6E6E` colors.

### ConnectionServicePanel
- Source: `Forms/SettingsForm.cs` (`BuildServiceGroup`)
- Category: basic
- Description: Listener toggle and local-key controls.
- Extractable props: `isServerEnabled`, `localConnectionKey`, `operationInProgress`; toggle/copy/regenerate actions.
- Hardcoded: labels, three-column table, margins.

### RemoteConnectionFields
- Source: `Forms/SettingsForm.cs` (`BuildRemoteGroup`)
- Category: basic
- Description: Peer IPv4 and six-character key inputs.
- Extractable props: `remoteIp`, `remoteKey`, validation state; `onSave`.
- Hardcoded: labels, placeholder, 15/6 limits, 94 px label column.

### SettingsHint
- Source: `Forms/SettingsForm.cs` (constructor hint label)
- Category: basic
- Description: Low-emphasis firewall safety advisory.
- Extractable props: none.
- Hardcoded: Chinese copy, Segoe UI 8.5 italic, `#737373`, 450 px maximum width.
