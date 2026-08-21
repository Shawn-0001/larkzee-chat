# Page dependency trees

WinForms has two meaningful visual surfaces. Trees include all project-local UI, state, service, and networking dependencies reachable from each form; .NET framework assemblies are omitted.

## Main chat window

Entry: `Forms/MainForm.cs`

Dependencies:

- `Forms/MainForm.cs`
  - `Controls/ChatMessageControl.cs`
  - `Forms/SettingsForm.cs`
    - `Forms/Ipv4InputValidation.cs`
    - `Models/AppSettings.cs`
    - `Networking/ChatSessionManager.cs`
      - `Networking/AuthenticationService.cs`
      - `Networking/ChatSessionContracts.cs`
      - `Networking/MessageProtocol.cs`
        - `Models/NetworkMessage.cs`
      - `Models/NetworkMessage.cs`
    - `Services/SettingsService.cs`
      - `Models/AppSettings.cs`
  - `Forms/Ipv4InputValidation.cs`
  - `Models/AppSettings.cs`
  - `Networking/ChatSessionManager.cs`
    - `Networking/AuthenticationService.cs`
    - `Networking/ChatSessionContracts.cs`
    - `Networking/MessageProtocol.cs`
      - `Models/NetworkMessage.cs`
    - `Models/NetworkMessage.cs`
  - `Services/SettingsService.cs`
    - `Models/AppSettings.cs`

Visual-first context candidates: `Forms/MainForm.cs`, `Controls/ChatMessageControl.cs`, `Forms/SettingsForm.cs`, and `.superdesign/init/theme.md`. Networking/service files are behavioral dependencies and should only be passed when a design task needs their states or error contracts.

## Settings modal

Entry: `Forms/SettingsForm.cs`

Dependencies:

- `Forms/SettingsForm.cs`
  - `Forms/Ipv4InputValidation.cs`
  - `Models/AppSettings.cs`
  - `Networking/ChatSessionManager.cs`
    - `Networking/AuthenticationService.cs`
    - `Networking/ChatSessionContracts.cs`
    - `Networking/MessageProtocol.cs`
      - `Models/NetworkMessage.cs`
    - `Models/NetworkMessage.cs`
  - `Services/SettingsService.cs`
    - `Models/AppSettings.cs`

Visual-first context candidates: `Forms/SettingsForm.cs`, `Forms/MainForm.cs` for its owning shell, and `.superdesign/init/theme.md`.

## Application bootstrap

Entry: `Program.cs`

Dependencies:

- `Program.cs`
  - `Forms/MainForm.cs` (full dependency tree above)
  - `Models/AppSettings.cs`
  - `Networking/ChatSessionManager.cs`
  - `Services/SettingsService.cs`

Use these trees as candidate sets, then apply payload budgeting; they are not an instruction to submit every backend file to a visual generation call.
