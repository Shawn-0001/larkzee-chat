# Window and navigation map

There are no URL routes and no router package. Navigation is native WinForms window flow.

## Route-equivalent map

| Route-equivalent surface | Entry/source | Parent/layout | Opened by | Summary |
|---|---|---|---|---|
| Application startup / main chat | `Forms/MainForm.cs` | Top-level WinForms application shell | `Program.Main` calls `Application.Run(mainForm)` | Persistent chat window with header, connection status/actions, message feed, and composer. |
| Settings modal | `Forms/SettingsForm.cs` | Modal child of `MainForm` | Configuration button or an attempted connection with missing/invalid peer settings calls `ShowDialog(this)` | Enables inbound service, exposes/copies/regenerates the process-local key, edits peer IPv4/key, and saves peer IP. |
| Friendly information dialog | Native `MessageBox` | Owned by the active form | Validation, connection, network, copy, or save failure | Short user-facing status/error text; not a navigable page. |

## Navigation graph

`Program.Main` -> `MainForm` -> `SettingsForm (modal)` -> returns to `MainForm`

- Closing `SettingsForm` does not close the main application or disable a listener the user enabled.
- Closing `MainForm` asynchronously disposes network resources, persists only safe settings, then exits the message loop.
- There is no back stack, sidebar, tab navigation, or deep link.

## Bootstrap configuration (full source)

WinForms has no router config file; `Program.cs` is the complete route-equivalent startup configuration.

```csharp
using System.Diagnostics;
using LarkzeeChat.Forms;
using LarkzeeChat.Models;
using LarkzeeChat.Networking;
using LarkzeeChat.Services;

namespace LarkzeeChat;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => ShowTopLevelError(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                ShowTopLevelError(exception);
            }
        };

        try
        {
            ApplicationConfiguration.Initialize();

            var settingsService = new SettingsService();
            AppSettings settings = settingsService.Load();
            // Keys are intentionally empty on every process launch.
            settings.RemoteKey = string.Empty;

            var sessionManager = new ChatSessionManager();
            try
            {
                var mainForm = new MainForm(sessionManager, settingsService, settings);
                Application.Run(mainForm);
            }
            finally
            {
                // MainForm normally disposes asynchronously during its close
                // sequence; idempotent cleanup is also the safe fallback if
                // Application.Run or form construction fails.
                sessionManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            ShowTopLevelError(exception);
        }
    }

    private static void ShowTopLevelError(Exception exception)
    {
        Debug.WriteLine($"Larkzee Chat unhandled UI exception: {exception}");

        try
        {
            if (Environment.UserInteractive)
            {
                MessageBox.Show(
                    "程序遇到问题，需要关闭。请重新启动 Larkzee Chat。",
                    "Larkzee Chat",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        catch
        {
            // There is no safe UI surface left for a second-level error.
        }
    }
}
```
