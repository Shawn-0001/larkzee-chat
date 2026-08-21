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

            var sessionManager = new ChatSessionManager();
            // Keep the manually configured local password available while the
            // listener is disabled. The settings form can replace it later;
            // an absent/invalid value makes EnableServerAsync fail cleanly.
            sessionManager.SetConnectionPassword(settings.LocalPassword);
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
