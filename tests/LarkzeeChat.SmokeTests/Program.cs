using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using LarkzeeChat.Controls;
using LarkzeeChat.Forms;
using LarkzeeChat.Models;
using LarkzeeChat.Networking;
using LarkzeeChat.Services;

internal static class Program
{
    public static async Task<int> Main()
    {
        var suite = new SmokeSuite();
        return await suite.RunAsync().ConfigureAwait(false);
    }
}

internal sealed class SmokeSuite
{
    private const int Port = 45678;
    private const int OperationTimeoutMilliseconds = 5_000;
    private const int ShortProbeTimeoutMilliseconds = 750;
    private const int WaitTimeoutMilliseconds = 5_000;
    private const int UiStateTimeoutMilliseconds = 30_000;

    private const string TestPassword = "Larkzee Manual Password 2026!";
    private const string ChangedTestPassword = "Larkzee Changed Password 2026!";

    private readonly List<string> _failures = [];
    private int _passed;

    public async Task<int> RunAsync()
    {
        Console.WriteLine("Larkzee Chat localhost smoke tests");
        Console.WriteLine($"Target: 127.0.0.1:{Port}");

        await RunCaseAsync("initial state, listener enable, and disable", TestInitialStateAndListenerAsync);
        await RunCaseAsync("initial main-window state", TestInitialMainWindowStateAsync);
        await RunCaseAsync("main-window visual structure and bounded bubbles", TestMainWindowVisualStructureAsync);
        await RunCaseAsync("message retention count and disk boundary", TestMessageRetentionCountAsync);
        await RunCaseAsync("message retention text budget", TestMessageRetentionTextBudgetAsync);
        await RunCaseAsync("message retention receipt age", TestMessageRetentionReceiptAgeAsync);
        await RunCaseAsync("main-window retention integration and disposal", TestMainWindowRetentionIntegrationAsync);
        await RunCaseAsync("normal main-window close cleans listener", TestNormalMainWindowCloseAsync);
        await RunCaseAsync("settings persist protected passwords and peer IP", TestSettingsPersistenceAsync);
        await RunCaseAsync("wrong password is rejected", TestWrongConnectionKeyAsync);
        await RunCaseAsync("authentication rate limit", TestAuthenticationRateLimitAsync);
        await RunCaseAsync("pre-auth transport failures are rate limited", TestPreAuthTransportFailureLimitAsync);
        await RunCaseAsync("fragmented and coalesced TCP frames", TestTcpFramingAsync);
        await RunCaseAsync("encrypted envelope tampering closes the session", TestEncryptedTamperingAsync);
        await RunCaseAsync("encrypted sequence replay and skip are rejected", TestEncryptedSequenceValidationAsync);
        await RunCaseAsync("encrypted sequence and reconnect key material are fresh", TestEncryptedSequenceAndReconnectAsync);
        await RunCaseAsync("clean connection, UTF-8 chat, busy rejection, and disconnect", TestChatAndConnectionLifecycleAsync);
        await RunCaseAsync("disabling the listener closes an inbound session", TestDisableListenerDisconnectsInboundAsync);
        await RunCaseAsync("manual password changes preserve current session and affect future handshakes", TestManualPasswordChangeAsync);

        Console.WriteLine();
        Console.WriteLine($"Summary: {_passed} passed, {_failures.Count} failed.");
        return _failures.Count == 0 ? 0 : 1;
    }

    private async Task RunCaseAsync(string name, Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception exception)
        {
            _failures.Add(name);
            Console.WriteLine($"[FAIL] {name}: {exception.Message}");
        }
    }

    private static async Task TestInitialStateAndListenerAsync()
    {
        ChatSessionManager? manager = null;
        try
        {
            manager = new ChatSessionManager();
            Assert(!manager.IsServerEnabled, "a new manager must have the server disabled");
            Assert(!manager.IsConnected, "a new manager must be disconnected");
            Assert(string.IsNullOrEmpty(manager.LocalConnectionKey), "a disabled server must not expose a key");

            ServerStartResult missingPassword = await WithTimeout(
                    manager.EnableServerAsync(),
                    "EnableServerAsync without a password")
                .ConfigureAwait(false);
            Assert(!missingPassword.Succeeded, "listener must not start without a configured manual password");

            ServerStartResult start = await EnableAsync(manager).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");
            Assert(manager.IsServerEnabled, "EnableServerAsync must enable the listener");
            Assert(IsValidManualPassword(RequireKey(manager, start)), "listener password must be valid");

            await DisableAsync(manager).ConfigureAwait(false);
            Assert(!manager.IsServerEnabled, "DisableServerAsync must stop the listener");
            Assert(await WaitForPortClosedAsync().ConfigureAwait(false), "port 45678 must reject new TCP connections after disable");
        }
        finally
        {
            await DisposeGroupAsync(manager).ConfigureAwait(false);
        }
    }

    private static async Task TestInitialMainWindowStateAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "LarkzeeChatSmoke",
                Guid.NewGuid().ToString("N"));
            var manager = new ChatSessionManager();

            try
            {
                var settingsService = new SettingsService(Path.Combine(temporaryRoot, "settings.json"));
                using var form = new MainForm(manager, settingsService, new AppSettings());
                List<Control> controls = Descendants(form).ToList();

                Assert(form.Text == "Larkzee Chat", "main-window title must be Larkzee Chat");
                Assert(controls.OfType<Label>().Any(label => label.Text == "● 未连接"),
                    "main window must initially show only the disconnected status");

                Button connectButton = controls.OfType<Button>().Single(button => button.Text == "连接");
                Button sendButton = controls.OfType<Button>().Single(button => button.Text == "发送");
                RichTextBox messageInput = controls.OfType<RichTextBox>().Single(textBox => textBox.Multiline);
                Assert(connectButton.Enabled, "connect button must initially be enabled");
                Assert(!sendButton.Enabled, "send button must initially be disabled");
                Assert(messageInput.Enabled, "message input must remain enabled while disconnected");
                Assert(!messageInput.AcceptsTab,
                    "Tab must keep native focus navigation while Shift+Enter owns multiline input");

                string visibleText = string.Join('|', controls.Select(control => control.Text));
                string[] forbiddenMainWindowText =
                [
                    "45678", "连接密钥", "对方 IP", "本机 IP", "服务端", "客户端", "Token"
                ];
                Assert(forbiddenMainWindowText.All(text => !visibleText.Contains(text, StringComparison.OrdinalIgnoreCase)),
                    "main window must not expose IP, key, role, port, or token details");
            }
            finally
            {
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            }
        }).ConfigureAwait(false);
    }

    private static async Task TestMainWindowVisualStructureAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "LarkzeeChatSmoke",
                Guid.NewGuid().ToString("N"));
            var manager = new ChatSessionManager();
            MainForm? form = null;

            try
            {
                var settingsService = new SettingsService(Path.Combine(temporaryRoot, "settings.json"));
                form = new MainForm(manager, settingsService, new AppSettings());
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-2000, -2000);
                form.Show();
                Application.DoEvents();
                List<Control> controls = Descendants(form).ToList();

                Assert(form.ClientSize == new Size(720, 620), "main window must default to 720x620 client size");
                Assert(form.MinimumSize == new Size(560, 450), "main window minimum size must be 560x450");

                Panel header = controls.OfType<Panel>().Single(panel => panel.AccessibleName == "聊天标题栏");
                Assert(header.Height == 70 && header.BackColor == Color.White,
                    "header must be a 70px white panel");
                Panel composer = controls.OfType<Panel>().Single(panel => panel.AccessibleName == "消息编辑区");
                Assert(composer.Height == 128 && composer.BackColor == Color.White,
                    "composer must be a 128px white panel");
                Panel headerDivider = controls.OfType<Panel>().Single(panel => panel.AccessibleName == "页眉分隔线");
                Assert(headerDivider.Height == 1 && headerDivider.BackColor == Color.FromArgb(220, 225, 232),
                    "header divider must use #DCE1E8");
                Label title = controls.OfType<Label>().Single(label => label.AccessibleName == "聊天标题");
                Assert(title.ForeColor == Color.FromArgb(24, 34, 48),
                    "window title must use #182230");
                Button settingsButton = controls.OfType<Button>().Single(button => button.AccessibleName == "打开配置");
                Assert(settingsButton.Text == "⚙ 配置" && settingsButton.Size == new Size(84, 32),
                    "header must expose the approved quiet 84x32 settings action");
                Button connectionButton = controls.OfType<Button>().Single(button => button.AccessibleName == "连接或断开连接");
                Assert(connectionButton.Text == "连接"
                    && connectionButton.BackColor == Color.FromArgb(24, 119, 210)
                    && connectionButton.ForeColor == Color.White
                    && connectionButton.FlatAppearance.BorderSize == 0,
                    "disconnected state must show the approved primary connect action");

                MethodInfo applyConnectionState = typeof(MainForm).GetMethod(
                    "ApplyConnectionState",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("ApplyConnectionState was not found");
                applyConnectionState.Invoke(form, [true]);
                Assert(connectionButton.Text == "断开"
                    && connectionButton.BackColor == Color.White
                    && connectionButton.ForeColor == Color.FromArgb(163, 58, 58)
                    && connectionButton.FlatAppearance.BorderSize == 1,
                    "connected state must use a quiet red-text disconnect action");
                applyConnectionState.Invoke(form, [false]);

                BufferedFlowLayoutPanel feed = controls.OfType<BufferedFlowLayoutPanel>().Single();
                Assert(header.Top == 0
                    && feed.Top == header.Bottom
                    && feed.Bottom == composer.Top
                    && composer.Bottom == form.ClientSize.Height,
                    "header, feed, and composer must tile the client area without overlap");
                Assert(feed.Padding == new Padding(16, 12, 16, 12),
                    "message feed must use 16x12 padding");
                Assert(feed.BackColor == Color.FromArgb(247, 248, 250),
                    "message feed must use #F7F8FA");

                RichTextBox messageInput = controls.OfType<RichTextBox>().Single();
                Assert(messageInput.Multiline && messageInput.MaxLength == 8_000,
                    "composer must be an 8000-character multiline RichTextBox");
                Assert(messageInput.ScrollBars == RichTextBoxScrollBars.None,
                    "composer must not show a permanent scrollbar for an empty draft");
                Assert(messageInput.AccessibleName == "消息输入框",
                    "composer input must have an accessible name");

                Button sendButton = controls.OfType<Button>().Single(button => button.Text == "发送");
                RoundedBorderPanel inputFrame = controls.OfType<RoundedBorderPanel>()
                    .Single(panel => panel.AccessibleName == "圆角消息输入框");
                Assert(inputFrame.Region is null && inputFrame.UsesVisualOnlyRounding,
                    "rounded input frame must not use a native Region");
                Assert(sendButton.Parent is Panel actionBar
                    && actionBar.Parent == inputFrame
                    && actionBar.Dock == DockStyle.Bottom
                    && sendButton.Bounds.Right == actionBar.ClientSize.Width,
                    "send button must live at the bottom-right inside the rounded editor");
                Assert(sendButton.Size == new Size(72, 30), "send button must be 72x30");
                Assert(sendButton.BackColor == Color.FromArgb(24, 119, 210),
                    "send button must use #1877D2");
                Assert(sendButton.AccessibleName == "发送消息", "send button must have an accessible name");

                MethodInfo addMessage = typeof(MainForm).GetMethod(
                    "AddMessage",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("AddMessage was not found");
                addMessage.Invoke(form, ["incoming\nmultiline", "12:34", false]);
                addMessage.Invoke(form, ["outgoing", "12:35", true]);
                addMessage.Invoke(form, [new string('界', 8_000), "12:36", false]);
                addMessage.Invoke(form, [new string('x', 8_000), "12:37", true]);
                // Keep a short sentinel after the deliberately oversized
                // messages so the test can prove the feed scrolls its final
                // row completely into view.
                addMessage.Invoke(form, ["消息末尾", "12:38", false]);
                Application.DoEvents();

                Assert(feed.Controls.Count == 5,
                    "the visible feed must contain the five synthetic messages");
                ChatMessageControl incoming = (ChatMessageControl)feed.Controls[0];
                ChatMessageControl outgoing = (ChatMessageControl)feed.Controls[1];
                Assert(incoming.BubbleBackColor == Color.White
                    && incoming.BubbleBorderColor == Color.FromArgb(225, 229, 234)
                    && incoming.BubbleCornerRadius is >= 10 and <= 12,
                    "incoming bubble colors and radius must match the approved style");
                Assert(outgoing.BubbleBackColor == Color.FromArgb(220, 238, 255)
                    && outgoing.BubbleBorderColor == Color.FromArgb(199, 225, 250)
                    && outgoing.BubbleCornerRadius is >= 10 and <= 12,
                    "outgoing bubble colors and radius must match the approved style");

                Size[] resizeSizes =
                [
                    new Size(560, 450),
                    new Size(720, 620),
                    new Size(900, 700),
                    new Size(560, 620),
                    new Size(720, 450)
                ];
                for (int pass = 0; pass < 30; pass++)
                {
                    form.ClientSize = resizeSizes[pass % resizeSizes.Length];
                    form.PerformLayout();
                    Application.DoEvents();

                    int feedWidth = Math.Max(1, feed.ClientSize.Width - feed.Padding.Horizontal);
                    Assert(!feed.HasHorizontalScrollBar,
                        $"message feed must not expose a horizontal scrollbar after resize pass {pass}");
                    foreach (ChatMessageControl message in feed.Controls.OfType<ChatMessageControl>())
                    {
                        Assert(message.Width == feedWidth,
                            $"message row width must follow the current feed viewport on resize pass {pass}");
                        Assert(message.Height > 0
                            && message.BubbleBounds.Left >= 0
                            && message.BubbleBounds.Right <= message.ClientSize.Width,
                            $"bubble bounds must remain valid on resize pass {pass}");

                        RoundedBorderPanel bubble = Descendants(message)
                            .OfType<RoundedBorderPanel>()
                            .Single();
                        Assert(bubble.Region is null && bubble.UsesVisualOnlyRounding,
                            "message bubbles must use draw-only rounding without a native Region");
                        foreach (Label label in bubble.Controls.OfType<Label>())
                        {
                            Assert(label.Bounds.Right <= bubble.ClientSize.Width
                                && label.Bounds.Bottom <= bubble.ClientSize.Height,
                                $"message label must remain fully inside its bubble on resize pass {pass}");
                        }
                    }
                }

                Assert(feed.VerticalScroll.Maximum > feed.VerticalScroll.LargeChange
                    && feed.DisplayRectangle.Height > feed.ClientSize.Height,
                    "long messages must extend the vertical display rectangle without horizontal overflow");
                ChatMessageControl last = feed.Controls.OfType<ChatMessageControl>().Last();
                feed.ScrollControlIntoView(last);
                Application.DoEvents();
                Assert(last.Top >= 0 && last.Bottom <= feed.ClientSize.Height,
                    $"the newest short sentinel must be scrollable fully into the visible feed "
                    + $"(top={last.Top}, bottom={last.Bottom}, feedHeight={feed.ClientSize.Height}, "
                    + $"scrollValue={feed.VerticalScroll.Value}, maximum={feed.VerticalScroll.Maximum}, "
                    + $"largeChange={feed.VerticalScroll.LargeChange})");

                // Add once more after the viewport, scrollbar, and explicit
                // scroll extent are already stable. Content-only changes must
                // still schedule a synchronization and reveal the newest row.
                addMessage.Invoke(form, ["稳定后新增", "12:39", true]);
                Application.DoEvents();
                ChatMessageControl stableStateLast = feed.Controls
                    .OfType<ChatMessageControl>()
                    .Last();
                Assert(stableStateLast.MessageText == "稳定后新增"
                    && stableStateLast.Top >= 0
                    && stableStateLast.Bottom <= feed.ClientSize.Height,
                    "a message added after layout settles must become fully visible");

                string? previewPath = Environment.GetEnvironmentVariable("LARKZEE_CHAT_PREVIEW_PATH");
                if (!string.IsNullOrWhiteSpace(previewPath))
                {
                    // Form.DrawToBitmap includes non-client chrome, so use the
                    // full window size or the composer's bottom edge would be
                    // omitted from the optional visual-verification artifact.
                    using var preview = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                    preview.Save(previewPath, ImageFormat.Png);
                }
            }
            finally
            {
                if (form is { IsDisposed: false })
                {
                    form.Hide();
                    form.Dispose();
                    Application.DoEvents();
                }

                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            }
        }).ConfigureAwait(false);
    }

    private static async Task TestMessageRetentionCountAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "LarkzeeChatSmoke",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            var buffer = new MessageRetentionBuffer();
            DateTimeOffset now = DateTimeOffset.Now;

            try
            {
                for (int index = 0; index < 501; index++)
                {
                    string text = $"message-{index}";
                    var control = new ChatMessageControl(text, "12:34", isOwnMessage: index % 2 == 0);
                    IReadOnlyList<MessageRetentionBuffer.Entry> removed = buffer.Add(
                        control,
                        text,
                        now.AddMilliseconds(index));
                    DisposeEntries(removed);
                }

                Assert(buffer.Count == MessageRetentionBuffer.MaximumMessageCount,
                    "501 messages must retain exactly the newest 500 controls");
                Assert(buffer.CharacterCount <= MessageRetentionBuffer.MaximumTextCharacters,
                    "count retention must also preserve the text budget");
                Assert(buffer.Entries.All(entry => !entry.Control.IsDisposed),
                    "retained controls must remain undisposed");
                Assert(Directory.GetFiles(temporaryRoot).Length == 0,
                    "message retention must not create disk history");
            }
            finally
            {
                buffer.Dispose();
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }).ConfigureAwait(false);
    }

    private static async Task TestMessageRetentionTextBudgetAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            var buffer = new MessageRetentionBuffer();
            DateTimeOffset now = DateTimeOffset.Now;

            try
            {
                const int messageLength = 8_000;
                for (int index = 0; index < 13; index++)
                {
                    string text = new string((char)('a' + (index % 10)), messageLength);
                    // The buffer owns the text budget independently from the
                    // visual payload; keep the synthetic control tiny so this
                    // deterministic retention test does not spend seconds
                    // measuring 8000-character GDI labels.
                    var control = new ChatMessageControl(string.Empty, "12:34", isOwnMessage: false);
                    DisposeEntries(buffer.Add(control, text, now.AddMilliseconds(index)));
                }

                Assert(buffer.CharacterCount <= MessageRetentionBuffer.MaximumTextCharacters,
                    "aggregate UTF-16 text must stay at or below 100000 characters");
                Assert(buffer.Count <= MessageRetentionBuffer.MaximumMessageCount,
                    "text-budget pruning must preserve the count bound");
                Assert(buffer.Entries.Sum(entry => entry.Text.Length) == buffer.CharacterCount,
                    "retained text count must match retained entries");
            }
            finally
            {
                buffer.Dispose();
            }
        }).ConfigureAwait(false);
    }

    private static async Task TestMessageRetentionReceiptAgeAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            var buffer = new MessageRetentionBuffer();
            DateTimeOffset now = DateTimeOffset.Now;

            try
            {
                string text = "older than one day";
                var control = new ChatMessageControl(text, "12:34", isOwnMessage: false);
                IReadOnlyList<MessageRetentionBuffer.Entry> removed = buffer.Add(
                    control,
                    text,
                    now - MessageRetentionBuffer.MaximumAge - TimeSpan.FromMinutes(1));
                DisposeEntries(removed);

                Assert(buffer.Count == 0, "a message received more than 24 hours ago must be pruned");
                Assert(control.IsDisposed, "pruned old control must be disposed by the UI retention owner");
                Assert(buffer.CharacterCount == 0, "old-message pruning must update aggregate characters");
            }
            finally
            {
                buffer.Dispose();
            }
        }).ConfigureAwait(false);
    }

    private static async Task TestMainWindowRetentionIntegrationAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "LarkzeeChatSmoke",
                Guid.NewGuid().ToString("N"));
            var manager = new ChatSessionManager();

            try
            {
                var settingsService = new SettingsService(Path.Combine(temporaryRoot, "settings.json"));
                using var form = new MainForm(manager, settingsService, new AppSettings());
                form.CreateControl();
                form.PerformLayout();

                MethodInfo addMessage = typeof(MainForm).GetMethod(
                    "AddMessage",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("AddMessage was not found");
                FlowLayoutPanel feed = Descendants(form).OfType<FlowLayoutPanel>().Single();

                for (int index = 0; index < 501; index++)
                {
                    addMessage.Invoke(form, [$"message-{index}", "12:34", index % 2 == 0]);
                }

                List<ChatMessageControl> retained = feed.Controls
                    .OfType<ChatMessageControl>()
                    .ToList();
                Assert(retained.Count == MessageRetentionBuffer.MaximumMessageCount,
                    "the live message feed must retain exactly the newest 500 controls");
                Assert(retained[0].MessageText == "message-1"
                    && retained[^1].MessageText == "message-500",
                    "the live feed must evict the oldest message and preserve order");

                form.Dispose();
                Assert(retained.All(control => control.IsDisposed),
                    "closing the main window must dispose every retained chat control");
                Assert(!File.Exists(settingsService.SettingsPath),
                    "disposing an in-memory conversation must not create a history file");
            }
            finally
            {
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            }
        }).ConfigureAwait(false);
    }

    private static void DisposeEntries(IReadOnlyList<MessageRetentionBuffer.Entry> entries)
    {
        foreach (MessageRetentionBuffer.Entry entry in entries)
        {
            entry.Control.Dispose();
        }
    }

    private static Task TestSettingsPersistenceAsync()
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "LarkzeeChatSmoke",
            Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(temporaryRoot, "settings.json");

        try
        {
            var service = new SettingsService(settingsPath);
            var settings = new AppSettings
            {
                RemoteIp = "192.168.1.100",
                LocalPassword = TestPassword,
                RemotePassword = ChangedTestPassword
            };

            Assert(service.Save(settings), "settings save must succeed in a writable local directory");
            string json = File.ReadAllText(settingsPath);
            Assert(json.Contains("192.168.1.100", StringComparison.Ordinal), "peer IP must be persisted");
            Assert(!json.Contains(settings.LocalPassword, StringComparison.Ordinal), "local password must never be persisted in plaintext");
            Assert(!json.Contains(settings.RemotePassword, StringComparison.Ordinal), "peer password must never be persisted in plaintext");
            Assert(json.Contains("LocalPasswordProtected", StringComparison.Ordinal), "protected local password field must be persisted");
            Assert(json.Contains("RemotePasswordProtected", StringComparison.Ordinal), "protected peer password field must be persisted");

            AppSettings loaded = service.Load();
            Assert(loaded.RemoteIp == settings.RemoteIp, "persisted peer IP must round-trip");
            Assert(loaded.LocalPassword == settings.LocalPassword, "local password must round-trip through DPAPI");
            Assert(loaded.RemotePassword == settings.RemotePassword, "peer password must round-trip through DPAPI");

            var persisted = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                ?? throw new InvalidOperationException("protected settings JSON could not be parsed");
            persisted["LocalPasswordProtected"] = "not-base64";
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(persisted));
            AppSettings corruptedSecret = service.Load();
            Assert(corruptedSecret.RemoteIp == settings.RemoteIp, "corrupted secret must preserve the valid peer IP");
            Assert(string.IsNullOrEmpty(corruptedSecret.LocalPassword), "corrupted protected secret must fail closed");
            Assert(corruptedSecret.RemotePassword == settings.RemotePassword, "independently valid protected secret must survive corruption of the other");

            File.WriteAllText(settingsPath, "{ malformed json");
            AppSettings fallback = service.Load();
            Assert(string.IsNullOrEmpty(fallback.RemoteIp), "malformed settings must fall back to defaults");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task TestNormalMainWindowCloseAsync()
    {
        await RunOnStaThreadAsync(() =>
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "LarkzeeChatSmoke",
                Guid.NewGuid().ToString("N"));
            var manager = new ChatSessionManager();

            try
            {
                Assert(manager.SetConnectionPassword(TestPassword), "normal-close test must configure a manual password");
                ServerStartResult start = manager.EnableServerAsync().GetAwaiter().GetResult();
                Assert(start.Succeeded && manager.IsServerEnabled,
                    "normal-close test must begin with an active listener");

                var settingsService = new SettingsService(Path.Combine(temporaryRoot, "settings.json"));
                using var form = new MainForm(manager, settingsService, new AppSettings())
                {
                    ShowInTaskbar = false,
                    Opacity = 0
                };
                using var closeTimer = new System.Windows.Forms.Timer { Interval = 150 };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Stop();
                    form.Close();
                };
                closeTimer.Start();
                Application.Run(form);

                Assert(!manager.IsServerEnabled, "normal window close must stop the listener");
                Assert(!manager.IsConnected, "normal window close must leave no chat session");
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            }
        }).ConfigureAwait(false);

        Assert(await WaitForPortClosedAsync().ConfigureAwait(false),
            "normal main-window close must release port 45678");
    }

    private static async Task TestWrongConnectionKeyAsync()
    {
        ChatSessionManager? server = null;
        ChatSessionManager? client = null;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");

            string key = RequireKey(server!, start);
            string wrongKey = ChangeOneKeyCharacter(key);
            client = new ChatSessionManager();

            ConnectResult result = await ConnectAsync(client!, wrongKey).ConfigureAwait(false);
            Assert(!result.Succeeded, "wrong-key authentication must fail");
            Assert(result.FailureReason == ConnectFailureReason.AuthenticationFailed,
                $"wrong-key authentication should return AuthenticationFailed, got {result.FailureReason}");
            Assert(!client.IsConnected, "wrong-key authentication must not establish a session");
        }
        finally
        {
            await DisposeGroupAsync(client, server).ConfigureAwait(false);
        }
    }

    private static async Task TestAuthenticationRateLimitAsync()
    {
        ChatSessionManager? server = null;
        var clients = new List<ChatSessionManager>();
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");

            string key = RequireKey(server!, start);
            string wrongKey = ChangeOneKeyCharacter(key);
            bool rateLimitedOnFifth = false;

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                ChatSessionManager client = new();
                clients.Add(client);

                ConnectResult result = await ConnectAsync(client, wrongKey).ConfigureAwait(false);
                Assert(!result.Succeeded, $"wrong attempt {attempt} must not connect");

                if (attempt < 5)
                {
                    Assert(result.FailureReason == ConnectFailureReason.AuthenticationFailed,
                        $"wrong attempt {attempt} should be AuthenticationFailed, got {result.FailureReason}");
                }
                else
                {
                    rateLimitedOnFifth = result.FailureReason == ConnectFailureReason.RateLimited;
                    Assert(rateLimitedOnFifth || result.FailureReason == ConnectFailureReason.AuthenticationFailed,
                        $"wrong attempt 5 should be AuthenticationFailed or RateLimited, got {result.FailureReason}");
                }
            }

            ChatSessionManager nextClient = new();
            clients.Add(nextClient);
            ConnectResult next = await ConnectAsync(nextClient, wrongKey).ConfigureAwait(false);
            Assert(!next.Succeeded, "the next new attempt after five failures must not connect");
            Assert(next.FailureReason == ConnectFailureReason.RateLimited,
                $"the next new attempt after five failures must be RateLimited (rate-limited on fifth={rateLimitedOnFifth}), got {next.FailureReason}");
        }
        finally
        {
            await DisposeGroupAsync(clients.Cast<ChatSessionManager?>().Append(server).ToArray()).ConfigureAwait(false);
        }
    }

    private static async Task TestTcpFramingAsync()
    {
        ChatSessionManager? server = null;
        EventHandler<ChatMessageReceivedEventArgs>? messageHandler = null;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");
            string key = RequireKey(server, start);

            using var rawClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await WithTimeout(rawClient.ConnectAsync(IPAddress.Loopback, Port), "raw TCP connect").ConfigureAwait(false);
            using NetworkStream stream = rawClient.GetStream();

            NetworkMessage challengeMessage = await ReadTestFrameAsync(stream).ConfigureAwait(false);
            Assert(challengeMessage.Type == "auth_challenge", "server must begin with AUTH_CHALLENGE");
            Assert(challengeMessage.Version == 2, "server challenge must use protocol version 2");
            byte[] challenge = Convert.FromBase64String(challengeMessage.Data ?? string.Empty);
            Assert(challenge.Length == 32, "authentication challenge must contain 32 random bytes");

            byte[] serverPublicKey = Convert.FromBase64String(challengeMessage.PublicKey ?? string.Empty);
            using ECDiffieHellman clientKeyAgreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] clientPublicKey = clientKeyAgreement.ExportSubjectPublicKeyInfo();
            byte[] transcriptHash = CreateTestTranscriptHash(challenge, serverPublicKey, clientPublicKey);
            byte[] response = ComputeTestProof(key, "LarkzeeChat/v2/client-proof", transcriptHash);

            byte[] responseFrame = CreateTestFrame(new NetworkMessage
            {
                Type = "auth_response",
                Version = 2,
                Data = Convert.ToBase64String(response),
                PublicKey = Convert.ToBase64String(clientPublicKey)
            });
            string responseJson = Encoding.UTF8.GetString(responseFrame.AsSpan(sizeof(int)));
            Assert(!responseJson.Contains(key, StringComparison.Ordinal), "authentication frame must not transmit the key");

            // Deliberately fragment both the four-byte header and body. The
            // server must keep reading until the entire frame is available.
            for (int offset = 0; offset < responseFrame.Length; offset += 3)
            {
                int count = Math.Min(3, responseFrame.Length - offset);
                await stream.WriteAsync(responseFrame.AsMemory(offset, count)).ConfigureAwait(false);
                await Task.Delay(1).ConfigureAwait(false);
            }

            NetworkMessage authResult = await ReadTestFrameAsync(stream).ConfigureAwait(false);
            Assert(authResult.Type == "auth_ok", "fragmented AUTH_RESPONSE must authenticate");
            Assert(authResult.Version == 2, "AUTH_OK must use protocol version 2");
            byte[] serverProof = Convert.FromBase64String(authResult.Data ?? string.Empty);
            byte[] expectedServerProof = ComputeTestProof(key, "LarkzeeChat/v2/server-proof", transcriptHash);
            Assert(serverProof.Length == 32 && CryptographicOperations.FixedTimeEquals(serverProof, expectedServerProof),
                "AUTH_OK must contain the authenticated server proof");
            TestSessionCipher cipher = CreateTestSessionCipher(clientKeyAgreement, serverPublicKey, transcriptHash);
            await WaitUntilAsync(() => server.IsConnected, "raw framed client connected").ConfigureAwait(false);

            var received = new TaskCompletionSource<ChatMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            messageHandler = (_, args) => received.TrySetResult(args);
            server.MessageReceived += messageHandler;

            const string text = "粘包与分包测试\n第二行";
            bool serverSent = await WithTimeout(server.SendMessageAsync(text), "encrypted server CHAT").ConfigureAwait(false);
            Assert(serverSent, "authenticated manager must send encrypted CHAT");
            NetworkMessage outboundEnvelope = await ReadTestFrameAsync(stream).ConfigureAwait(false);
            Assert(outboundEnvelope.Type == "encrypted" && outboundEnvelope.Version == 2,
                "post-auth manager CHAT must use the encrypted v2 envelope");
            Assert(outboundEnvelope.Text is null && outboundEnvelope.Timestamp is null && outboundEnvelope.Reason is null,
                "encrypted envelope must not expose plaintext message fields");
            string outboundJson = JsonSerializer.Serialize(outboundEnvelope);
            Assert(!outboundJson.Contains(text, StringComparison.Ordinal),
                "raw post-auth JSON must not contain plaintext CHAT text");
            NetworkMessage outboundInner = cipher.Decrypt(outboundEnvelope);
            Assert(outboundInner.Type == "chat" && outboundInner.Text == text,
                "encrypted manager CHAT must decrypt with the negotiated session key");

            NetworkMessage chatEnvelope = cipher.Encrypt(new NetworkMessage
            {
                Type = "chat",
                Text = text,
                Timestamp = DateTimeOffset.Now
            }, 0);
            NetworkMessage disconnectEnvelope = cipher.Encrypt(new NetworkMessage { Type = "disconnect" }, 1);
            byte[] chatFrame = CreateTestFrame(chatEnvelope);
            byte[] disconnectFrame = CreateTestFrame(disconnectEnvelope);
            byte[] coalesced = new byte[chatFrame.Length + disconnectFrame.Length];
            chatFrame.CopyTo(coalesced, 0);
            disconnectFrame.CopyTo(coalesced, chatFrame.Length);

            // Deliver two complete frames in one socket write. The server must
            // consume exactly one frame at a time and preserve the CHAT body.
            await stream.WriteAsync(coalesced).ConfigureAwait(false);
            ChatMessageReceivedEventArgs chat = await WithTimeout(received.Task, "coalesced CHAT receive").ConfigureAwait(false);
            Assert(chat.Text == text, "coalesced framed CHAT must preserve UTF-8 and newlines");
            await WaitUntilAsync(() => !server.IsConnected, "coalesced DISCONNECT processed").ConfigureAwait(false);
        }
        finally
        {
            if (server is not null && messageHandler is not null)
            {
                server.MessageReceived -= messageHandler;
            }

            await DisposeGroupAsync(server).ConfigureAwait(false);
        }
    }

    private static async Task TestEncryptedTamperingAsync()
    {
        ChatSessionManager? server = null;
        EventHandler<ChatMessageReceivedEventArgs>? messageHandler = null;
        int receivedCount = 0;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "tamper test listener must start");
            using RawPeerSession peer = await AuthenticateRawPeerAsync(RequireKey(server, start)).ConfigureAwait(false);

            messageHandler = (_, _) => Interlocked.Increment(ref receivedCount);
            server.MessageReceived += messageHandler;
            NetworkMessage tampered = peer.Cipher.Encrypt(
                new NetworkMessage
                {
                    Type = "chat",
                    Text = "这段明文不应被送达",
                    Timestamp = DateTimeOffset.Now
                },
                0);
            string ciphertext = tampered.Data ?? string.Empty;
            char replacement = ciphertext[0] == 'A' ? 'B' : 'A';
            tampered.Data = replacement + ciphertext[1..];
            await peer.Stream.WriteAsync(CreateTestFrame(tampered)).ConfigureAwait(false);

            await WaitUntilAsync(() => !server.IsConnected, "tampered encrypted frame closes session").ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            Assert(Volatile.Read(ref receivedCount) == 0, "tampered encrypted payload must never raise plaintext CHAT");
        }
        finally
        {
            if (server is not null && messageHandler is not null)
            {
                server.MessageReceived -= messageHandler;
            }

            await DisposeGroupAsync(server).ConfigureAwait(false);
        }
    }

    private static async Task TestEncryptedSequenceValidationAsync()
    {
        ChatSessionManager? server = null;
        EventHandler<ChatMessageReceivedEventArgs>? messageHandler = null;
        int receivedCount = 0;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "sequence test listener must start");
            string key = RequireKey(server, start);
            using (RawPeerSession peer = await AuthenticateRawPeerAsync(key).ConfigureAwait(false))
            {
                messageHandler = (_, _) => Interlocked.Increment(ref receivedCount);
                server.MessageReceived += messageHandler;
                NetworkMessage valid = peer.Cipher.Encrypt(
                    new NetworkMessage { Type = "chat", Text = "一次" },
                    0);
                await peer.Stream.WriteAsync(CreateTestFrame(valid)).ConfigureAwait(false);
                await WaitUntilAsync(() => Volatile.Read(ref receivedCount) == 1, "first encrypted CHAT").ConfigureAwait(false);

                NetworkMessage replay = peer.Cipher.Encrypt(
                    new NetworkMessage { Type = "chat", Text = "重复" },
                    0);
                await peer.Stream.WriteAsync(CreateTestFrame(replay)).ConfigureAwait(false);
                await WaitUntilAsync(() => !server.IsConnected, "replayed encrypted sequence closes session").ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);
                Assert(Volatile.Read(ref receivedCount) == 1, "replayed sequence must not deliver a second CHAT");
            }

            using RawPeerSession skippedPeer = await AuthenticateRawPeerAsync(key).ConfigureAwait(false);
            receivedCount = 0;
            NetworkMessage skipped = skippedPeer.Cipher.Encrypt(
                new NetworkMessage { Type = "chat", Text = "跳过" },
                1);
            await skippedPeer.Stream.WriteAsync(CreateTestFrame(skipped)).ConfigureAwait(false);
            await WaitUntilAsync(() => !server.IsConnected, "skipped encrypted sequence closes session").ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            Assert(Volatile.Read(ref receivedCount) == 0, "skipped sequence must not deliver a CHAT");
        }
        finally
        {
            if (server is not null && messageHandler is not null)
            {
                server.MessageReceived -= messageHandler;
            }

            await DisposeGroupAsync(server).ConfigureAwait(false);
        }
    }

    private static async Task TestEncryptedSequenceAndReconnectAsync()
    {
        ChatSessionManager? server = null;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "fresh-session test listener must start");
            string key = RequireKey(server, start);
            byte[] firstServerPublicKey;

            using (RawPeerSession firstPeer = await AuthenticateRawPeerAsync(key).ConfigureAwait(false))
            {
                bool firstSent = await WithTimeout(server.SendMessageAsync("第一条"), "first encrypted outbound CHAT").ConfigureAwait(false);
                Assert(firstSent, "first outbound encrypted CHAT must succeed");
                NetworkMessage first = await ReadTestFrameAsync(firstPeer.Stream).ConfigureAwait(false);
                bool secondSent = await WithTimeout(server.SendMessageAsync("第二条"), "second encrypted outbound CHAT").ConfigureAwait(false);
                Assert(secondSent, "second outbound encrypted CHAT must succeed");
                NetworkMessage second = await ReadTestFrameAsync(firstPeer.Stream).ConfigureAwait(false);
                Assert(first.Type == "encrypted" && second.Type == "encrypted", "outbound post-auth frames must be encrypted");
                Assert(first.Sequence == 0 && second.Sequence == 1, "outbound encrypted sequences must advance exactly");
                Assert(!string.Equals(first.Data, second.Data, StringComparison.Ordinal)
                    || !string.Equals(first.Tag, second.Tag, StringComparison.Ordinal),
                    "outbound encrypted frames must have distinct ciphertext or tags");
                byte[] firstNonce = firstPeer.Cipher.GetReceiveNonce(first.Sequence!.Value);
                byte[] secondNonce = firstPeer.Cipher.GetReceiveNonce(second.Sequence!.Value);
                Assert(!firstNonce.AsSpan().SequenceEqual(secondNonce), "outbound encrypted frames must use distinct nonces");

                byte[] disconnectFrame = CreateTestFrame(firstPeer.Cipher.Encrypt(
                    new NetworkMessage { Type = "disconnect" },
                    0));
                await firstPeer.Stream.WriteAsync(disconnectFrame).ConfigureAwait(false);
                await WaitUntilAsync(() => !server.IsConnected, "first encrypted session disconnect").ConfigureAwait(false);
                Assert(firstPeer.ServerPublicKey.Length > 0, "first handshake must expose server public key evidence");
                firstServerPublicKey = firstPeer.ServerPublicKey.ToArray();
            }

            using RawPeerSession secondPeer = await AuthenticateRawPeerAsync(key).ConfigureAwait(false);
            Assert(!secondPeer.ServerPublicKey.AsSpan().SequenceEqual(firstServerPublicKey),
                "reconnect must generate fresh ephemeral server key material");
        }
        finally
        {
            await DisposeGroupAsync(server).ConfigureAwait(false);
        }
    }

    private static async Task TestPreAuthTransportFailureLimitAsync()
    {
        ChatSessionManager? server = null;
        ChatSessionManager? blockedClient = null;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");

            // Four malformed frames exercise protocol-error accounting.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                using var malformedClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                await malformedClient.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
                using NetworkStream stream = malformedClient.GetStream();
                NetworkMessage challenge = await ReadTestFrameAsync(stream).ConfigureAwait(false);
                Assert(challenge.Type == "auth_challenge", "malformed attempt must receive a challenge");

                byte[] invalidLength = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32BigEndian(invalidLength, 64 * 1024 + 1);
                await stream.WriteAsync(invalidLength).ConfigureAwait(false);
                await Task.Delay(75).ConfigureAwait(false);
            }

            // The fifth attempt stays silent until the server's bounded auth
            // timeout. It must count as a failure unless the listener itself
            // was cancelled.
            using (var silentClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true })
            {
                await silentClient.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
                using NetworkStream stream = silentClient.GetStream();
                NetworkMessage challenge = await ReadTestFrameAsync(stream).ConfigureAwait(false);
                Assert(challenge.Type == "auth_challenge", "silent attempt must receive a challenge");
                await Task.Delay(TimeSpan.FromSeconds(16)).ConfigureAwait(false);
            }

            await Task.Delay(100).ConfigureAwait(false);
            blockedClient = new ChatSessionManager();
            ConnectResult blocked = await ConnectAsync(blockedClient, RequireKey(server, start)).ConfigureAwait(false);
            Assert(!blocked.Succeeded && blocked.FailureReason == ConnectFailureReason.RateLimited,
                $"sixth attempt after pre-auth failures must be RateLimited, got {blocked.FailureReason}");
        }
        finally
        {
            await DisposeGroupAsync(blockedClient, server).ConfigureAwait(false);
        }
    }

    private static async Task TestChatAndConnectionLifecycleAsync()
    {
        ChatSessionManager? server = null;
        ChatSessionManager? client = null;
        ChatSessionManager? thirdClient = null;
        EventHandler<ConnectionStateChangedEventArgs>? serverStateHandler = null;
        EventHandler<ConnectionStateChangedEventArgs>? clientStateHandler = null;
        int connectedStateEvents = 0;
        int disconnectedStateEvents = 0;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");
            string key = RequireKey(server!, start);

            client = new ChatSessionManager();
            serverStateHandler = (_, args) =>
            {
                if (args.IsConnected)
                {
                    Interlocked.Increment(ref connectedStateEvents);
                }
                else
                {
                    Interlocked.Increment(ref disconnectedStateEvents);
                }
            };
            clientStateHandler = (_, args) =>
            {
                if (args.IsConnected)
                {
                    Interlocked.Increment(ref connectedStateEvents);
                }
                else
                {
                    Interlocked.Increment(ref disconnectedStateEvents);
                }
            };
            server.ConnectionStateChanged += serverStateHandler;
            client.ConnectionStateChanged += clientStateHandler;

            ConnectResult connect = await ConnectAsync(client!, key).ConfigureAwait(false);
            Assert(connect.Succeeded, $"correct-key connection should succeed, got {connect.FailureReason}");
            await WaitUntilAsync(() => server!.IsConnected && client!.IsConnected, "both peers connected").ConfigureAwait(false);
            await WaitUntilAsync(() => Volatile.Read(ref connectedStateEvents) >= 2, "ConnectionStateChanged connected events").ConfigureAwait(false);

            const string unicodeText = "你好，这是测试消息。";
            string received = await SendAndReceiveAsync(client!, server!, unicodeText).ConfigureAwait(false);
            Assert(received == unicodeText, "the Chinese CHAT payload must round-trip exactly as UTF-8");

            const string multilineText = "第一行\n第二行\r\n第三行";
            string multilineReceived = await SendAndReceiveAsync(server!, client!, multilineText).ConfigureAwait(false);
            Assert(multilineReceived == multilineText, "multiline CHAT payload must round-trip without normalization");

            bool oversizedSent = await SendAsync(client!, new string('界', 30_000)).ConfigureAwait(false);
            Assert(!oversizedSent, "a CHAT frame larger than 64 KiB must be rejected locally");
            Assert(server!.IsConnected && client!.IsConnected,
                "rejecting an oversized local message must not disconnect the valid session");

            thirdClient = new ChatSessionManager();
            ConnectResult duplicate = await ConnectAsync(thirdClient!, key).ConfigureAwait(false);
            Assert(!duplicate.Succeeded, "a third client must not establish a second session");
            Assert(duplicate.FailureReason is ConnectFailureReason.RemoteBusy or ConnectFailureReason.AlreadyConnected,
                $"third-client rejection should be RemoteBusy or AlreadyConnected, got {duplicate.FailureReason}");
            Assert(!thirdClient!.IsConnected, "a rejected third client must remain disconnected");
            Assert(server!.IsConnected && client!.IsConnected, "rejecting a third client must not replace the current session");

            await DisconnectAsync(client!).ConfigureAwait(false);
            await WaitUntilAsync(() => !server!.IsConnected && !client!.IsConnected && !thirdClient!.IsConnected, "both peers disconnected").ConfigureAwait(false);
            await WaitUntilAsync(() => Volatile.Read(ref disconnectedStateEvents) >= 2, "ConnectionStateChanged disconnected events").ConfigureAwait(false);
        }
        finally
        {
            if (server is not null && serverStateHandler is not null)
            {
                server.ConnectionStateChanged -= serverStateHandler;
            }

            if (client is not null && clientStateHandler is not null)
            {
                client.ConnectionStateChanged -= clientStateHandler;
            }

            await DisposeGroupAsync(thirdClient, client, server).ConfigureAwait(false);
        }
    }

    private static async Task TestDisableListenerDisconnectsInboundAsync()
    {
        ChatSessionManager? server = null;
        ChatSessionManager? client = null;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");
            client = new ChatSessionManager();
            ConnectResult connect = await ConnectAsync(client, RequireKey(server, start)).ConfigureAwait(false);
            Assert(connect.Succeeded, "correct key must establish the inbound session");
            await WaitUntilAsync(() => server.IsConnected && client.IsConnected, "inbound session connected").ConfigureAwait(false);

            FieldInfo setupGateField = typeof(ChatSessionManager).GetField(
                "_connectionSetupGate",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("setup gate field was not found");
            var setupGate = (SemaphoreSlim)(setupGateField.GetValue(server)
                ?? throw new InvalidOperationException("setup gate value was null"));
            bool gateHeld = false;
            try
            {
                await setupGate.WaitAsync().ConfigureAwait(false);
                gateHeld = true;
                using var cancellation = new CancellationTokenSource();
                Task disableTask = server.DisableServerAsync(cancellation.Token);
                await WaitUntilAsync(() => !server.IsServerEnabled, "listener state changed to disabled").ConfigureAwait(false);
                cancellation.Cancel();
                await Task.Delay(75).ConfigureAwait(false);
                Assert(!disableTask.IsCompleted,
                    "disable cleanup must ignore cancellation after listener state is already OFF");

                setupGate.Release();
                gateHeld = false;
                await WithTimeout(disableTask, "non-cancellable listener cleanup").ConfigureAwait(false);
            }
            finally
            {
                if (gateHeld)
                {
                    setupGate.Release();
                }
            }

            Assert(!server.IsServerEnabled, "listener must be disabled");
            Assert(string.IsNullOrEmpty(server.LocalConnectionKey), "disabled listener must invalidate its key");
            await WaitUntilAsync(() => !server.IsConnected && !client.IsConnected, "listener-disable disconnect").ConfigureAwait(false);
            Assert(await WaitForPortClosedAsync().ConfigureAwait(false), "port 45678 must close after listener disable");
        }
        finally
        {
            await DisposeGroupAsync(client, server).ConfigureAwait(false);
        }
    }

    private static async Task TestManualPasswordChangeAsync()
    {
        ChatSessionManager? server = null;
        ChatSessionManager? client = null;
        ChatSessionManager? oldKeyClient = null;
        ChatSessionManager? freshKeyClient = null;
        try
        {
            server = new ChatSessionManager();
            ServerStartResult start = await EnableAsync(server).ConfigureAwait(false);
            Assert(start.Succeeded, "EnableServerAsync must succeed");
            string oldKey = RequireKey(server!, start);

            client = new ChatSessionManager();
            ConnectResult connect = await ConnectAsync(client!, oldKey).ConfigureAwait(false);
            Assert(connect.Succeeded, $"correct-key connection should succeed, got {connect.FailureReason}");
            await WaitUntilAsync(() => server!.IsConnected && client!.IsConnected, "regeneration test session connected").ConfigureAwait(false);

            Assert(server!.SetConnectionPassword(ChangedTestPassword), "changing the manual password must succeed");
            Assert(server.LocalPassword == ChangedTestPassword, "manager must expose the changed local password");
            Assert(server!.IsConnected && client!.IsConnected, "changing a password must not force-disconnect the existing session");

            await DisconnectAsync(client!).ConfigureAwait(false);
            await WaitUntilAsync(() => !server!.IsConnected && !client!.IsConnected, "regeneration test session disconnected").ConfigureAwait(false);

            oldKeyClient = new ChatSessionManager();
            ConnectResult oldKeyResult = await ConnectAsync(oldKeyClient!, oldKey).ConfigureAwait(false);
            Assert(!oldKeyResult.Succeeded, "the old password must fail for a future connection");
            Assert(oldKeyResult.FailureReason == ConnectFailureReason.AuthenticationFailed,
                $"the old key should return AuthenticationFailed, got {oldKeyResult.FailureReason}");

            freshKeyClient = new ChatSessionManager();
            ConnectResult freshResult = await ConnectAsync(freshKeyClient!, ChangedTestPassword).ConfigureAwait(false);
            Assert(freshResult.Succeeded, $"the changed password should authenticate, got {freshResult.FailureReason}");
            await WaitUntilAsync(() => server!.IsConnected && freshKeyClient!.IsConnected, "changed-password session connected").ConfigureAwait(false);
        }
        finally
        {
            await DisposeGroupAsync(freshKeyClient, oldKeyClient, client, server).ConfigureAwait(false);
        }
    }

    private static async Task<string> SendAndReceiveAsync(
        ChatSessionManager sender,
        ChatSessionManager receiver,
        string text)
    {
        var received = new TaskCompletionSource<ChatMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ChatMessageReceivedEventArgs> handler = (_, args) => received.TrySetResult(args);
        receiver.MessageReceived += handler;
        try
        {
            bool sent = await SendAsync(sender, text).ConfigureAwait(false);
            Assert(sent, "SendMessageAsync must report success while connected");
            ChatMessageReceivedEventArgs args = await WithTimeout(received.Task, "MessageReceived").ConfigureAwait(false);
            return args.Text;
        }
        finally
        {
            receiver.MessageReceived -= handler;
        }
    }

    private static async Task<ServerStartResult> EnableAsync(ChatSessionManager manager)
    {
        Assert(manager.SetConnectionPassword(TestPassword), "test manager must accept the manual password");
        return await WithTimeout(manager.EnableServerAsync(), "EnableServerAsync").ConfigureAwait(false);
    }

    private static async Task DisableAsync(ChatSessionManager manager)
    {
        await WithTimeout(manager.DisableServerAsync(), "DisableServerAsync").ConfigureAwait(false);
    }

    private static async Task<ConnectResult> ConnectAsync(ChatSessionManager manager, string key)
    {
        return await WithTimeout(manager.ConnectAsync("127.0.0.1", key), "ConnectAsync").ConfigureAwait(false);
    }

    private static async Task<bool> SendAsync(ChatSessionManager manager, string text)
    {
        return await WithTimeout(manager.SendMessageAsync(text), "SendMessageAsync").ConfigureAwait(false);
    }

    private static async Task DisconnectAsync(ChatSessionManager manager)
    {
        await WithTimeout(manager.DisconnectAsync(), "DisconnectAsync").ConfigureAwait(false);
    }

    private static async Task DisposeGroupAsync(params ChatSessionManager?[] managers)
    {
        List<Exception> errors = [];
        HashSet<ChatSessionManager> disposed = [];
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (ChatSessionManager? manager in managers)
        {
            if (manager is null || !disposed.Add(manager))
            {
                continue;
            }

            try
            {
                int remainingMilliseconds = Math.Max(
                    1,
                    OperationTimeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
                await WithTimeout(
                        manager.DisposeAsync().AsTask(),
                        "DisposeAsync",
                        remainingMilliseconds)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("One or more managers did not dispose cleanly", errors);
        }
    }

    private static async Task<bool> WaitForPortClosedAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < OperationTimeoutMilliseconds)
        {
            if (!await TryConnectTcpAsync().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> TryConnectTcpAsync()
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            Task connect = client.ConnectAsync(IPAddress.Loopback, Port);
            Task completed = await Task.WhenAny(connect, Task.Delay(ShortProbeTimeoutMilliseconds)).ConfigureAwait(false);
            if (completed != connect)
            {
                ObserveFaults(connect);
                return false;
            }

            await connect.ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static byte[] CreateTestFrame(NetworkMessage message)
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, options);
        byte[] frame = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), body.Length);
        body.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static byte[] CreateTestTranscriptHash(
        byte[] challenge,
        byte[] serverPublicKey,
        byte[] clientPublicKey)
    {
        byte[] version = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, 2);
        byte[] transcript = BuildTestLengthPrefixed(
            Encoding.UTF8.GetBytes("LarkzeeChat/v2/auth-transcript"),
            version,
            challenge,
            serverPublicKey,
            clientPublicKey);
        return SHA256.HashData(transcript);
    }

    private static byte[] ComputeTestProof(string key, string role, byte[] transcriptHash)
    {
        byte[] proofInput = BuildTestLengthPrefixed(
            Encoding.UTF8.GetBytes(role),
            transcriptHash);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return hmac.ComputeHash(proofInput);
    }

    private static byte[] BuildTestLengthPrefixed(params byte[][] fields)
    {
        int length = fields.Sum(field => sizeof(int) + field.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] field in fields)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, sizeof(int)), field.Length);
            offset += sizeof(int);
            field.CopyTo(result, offset);
            offset += field.Length;
        }

        return result;
    }

    private static TestSessionCipher CreateTestSessionCipher(
        ECDiffieHellman clientKeyAgreement,
        byte[] serverPublicKey,
        byte[] transcriptHash)
    {
        using ECDiffieHellman serverKeyAgreement = ECDiffieHellman.Create();
        serverKeyAgreement.ImportSubjectPublicKeyInfo(serverPublicKey, out int bytesRead);
        Assert(bytesRead == serverPublicKey.Length, "server public key must be a complete SPKI");
        byte[] sharedSecret = clientKeyAgreement.DeriveRawSecretAgreement(serverKeyAgreement.PublicKey);
        try
        {
            byte[] material = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sharedSecret,
                72,
                transcriptHash,
                Encoding.UTF8.GetBytes("LarkzeeChat/v2/session-keys"));
            try
            {
                return new TestSessionCipher(material);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static async Task<RawPeerSession> AuthenticateRawPeerAsync(string key)
    {
        TcpClient client = new(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await WithTimeout(client.ConnectAsync(IPAddress.Loopback, Port), "raw authenticated peer connect")
                .ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            NetworkMessage challengeMessage = await ReadTestFrameAsync(stream).ConfigureAwait(false);
            Assert(challengeMessage.Type == "auth_challenge" && challengeMessage.Version == 2,
                "raw peer must receive a v2 auth challenge");
            byte[] challenge = Convert.FromBase64String(challengeMessage.Data ?? string.Empty);
            byte[] serverPublicKey = Convert.FromBase64String(challengeMessage.PublicKey ?? string.Empty);
            using ECDiffieHellman clientKeyAgreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] clientPublicKey = clientKeyAgreement.ExportSubjectPublicKeyInfo();
            byte[] transcriptHash = CreateTestTranscriptHash(challenge, serverPublicKey, clientPublicKey);
            byte[] response = ComputeTestProof(key, "LarkzeeChat/v2/client-proof", transcriptHash);
            await stream.WriteAsync(CreateTestFrame(new NetworkMessage
            {
                Type = "auth_response",
                Version = 2,
                Data = Convert.ToBase64String(response),
                PublicKey = Convert.ToBase64String(clientPublicKey)
            })).ConfigureAwait(false);

            NetworkMessage authResult = await ReadTestFrameAsync(stream).ConfigureAwait(false);
            Assert(authResult.Type == "auth_ok" && authResult.Version == 2,
                "raw peer must receive a v2 auth result");
            byte[] serverProof = Convert.FromBase64String(authResult.Data ?? string.Empty);
            byte[] expectedServerProof = ComputeTestProof(key, "LarkzeeChat/v2/server-proof", transcriptHash);
            Assert(serverProof.Length == 32 && CryptographicOperations.FixedTimeEquals(serverProof, expectedServerProof),
                "raw peer must validate the server proof");
            TestSessionCipher cipher = CreateTestSessionCipher(clientKeyAgreement, serverPublicKey, transcriptHash);
            return new RawPeerSession(client, stream, cipher, serverPublicKey);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class RawPeerSession : IDisposable
    {
        internal RawPeerSession(
            TcpClient client,
            NetworkStream stream,
            TestSessionCipher cipher,
            byte[] serverPublicKey)
        {
            Client = client;
            Stream = stream;
            Cipher = cipher;
            ServerPublicKey = serverPublicKey;
        }

        internal TcpClient Client { get; }

        internal NetworkStream Stream { get; }

        internal TestSessionCipher Cipher { get; }

        internal byte[] ServerPublicKey { get; }

        public void Dispose()
        {
            Cipher.Dispose();
            Stream.Dispose();
            Client.Dispose();
        }
    }

    private sealed class TestSessionCipher : IDisposable
    {
        private readonly AesGcm _sendCipher;
        private readonly AesGcm _receiveCipher;
        private readonly byte[] _sendNoncePrefix;
        private readonly byte[] _receiveNoncePrefix;

        internal TestSessionCipher(byte[] material)
        {
            _sendCipher = new AesGcm(material.AsSpan(0, 32), 16);
            _receiveCipher = new AesGcm(material.AsSpan(32, 32), 16);
            _sendNoncePrefix = material.AsSpan(64, 4).ToArray();
            _receiveNoncePrefix = material.AsSpan(68, 4).ToArray();
        }

        internal NetworkMessage Encrypt(NetworkMessage inner, long sequence)
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(inner, options);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            byte[] nonce = BuildNonce(_sendNoncePrefix, sequence);
            byte[] associatedData = BuildTestAssociatedData(sequence);
            _sendCipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return new NetworkMessage
            {
                Type = "encrypted",
                Version = 2,
                Sequence = sequence,
                Data = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            };
        }

        internal NetworkMessage Decrypt(NetworkMessage envelope)
        {
            if (envelope.Sequence is not long sequence || sequence < 0)
            {
                throw new InvalidDataException("encrypted envelope must carry a sequence");
            }
            byte[] ciphertext = Convert.FromBase64String(envelope.Data ?? string.Empty);
            byte[] tag = Convert.FromBase64String(envelope.Tag ?? string.Empty);
            byte[] plaintext = new byte[ciphertext.Length];
            byte[] nonce = BuildNonce(_receiveNoncePrefix, sequence);
            byte[] associatedData = BuildTestAssociatedData(sequence);
            _receiveCipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return JsonSerializer.Deserialize<NetworkMessage>(plaintext)
                ?? throw new InvalidDataException("encrypted test payload was empty");
        }

        internal byte[] GetReceiveNonce(long sequence)
        {
            return BuildNonce(_receiveNoncePrefix, sequence);
        }

        public void Dispose()
        {
            _sendCipher.Dispose();
            _receiveCipher.Dispose();
            CryptographicOperations.ZeroMemory(_sendNoncePrefix);
            CryptographicOperations.ZeroMemory(_receiveNoncePrefix);
        }

        private static byte[] BuildNonce(byte[] prefix, long sequence)
        {
            byte[] nonce = new byte[12];
            prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence);
            return nonce;
        }

        private static byte[] BuildTestAssociatedData(long sequence)
        {
            byte[] version = new byte[sizeof(int)];
            byte[] sequenceBytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt32BigEndian(version, 2);
            BinaryPrimitives.WriteInt64BigEndian(sequenceBytes, sequence);
            return BuildTestLengthPrefixed(
                Encoding.UTF8.GetBytes("LarkzeeChat/v2/encrypted-envelope"),
                version,
                sequenceBytes);
        }
    }

    private static async Task<NetworkMessage> ReadTestFrameAsync(NetworkStream stream)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert(length is > 0 and <= 64 * 1024, $"received invalid frame length {length}");

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NetworkMessage>(body)
            ?? throw new InvalidDataException("received an empty JSON frame");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds >= WaitTimeoutMilliseconds)
            {
                throw new TimeoutException($"Timed out waiting for {description}");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static async Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "LarkzeeChat.SmokeTests.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await WithTimeout(completion.Task, "STA UI state check", UiStateTimeoutMilliseconds).ConfigureAwait(false);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> operation, string description)
    {
        Task completed = await Task.WhenAny(operation, Task.Delay(OperationTimeoutMilliseconds)).ConfigureAwait(false);
        if (completed != operation)
        {
            ObserveFaults(operation);
            throw new TimeoutException($"{description} exceeded {OperationTimeoutMilliseconds} ms");
        }

        return await operation.ConfigureAwait(false);
    }

    private static async Task WithTimeout(Task operation, string description)
    {
        await WithTimeout(operation, description, OperationTimeoutMilliseconds).ConfigureAwait(false);
    }

    private static async Task WithTimeout(Task operation, string description, int timeoutMilliseconds)
    {
        Task completed = await Task.WhenAny(operation, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);
        if (completed != operation)
        {
            ObserveFaults(operation);
            throw new TimeoutException($"{description} exceeded {timeoutMilliseconds} ms");
        }

        await operation.ConfigureAwait(false);
    }

    private static void ObserveFaults(Task operation)
    {
        _ = operation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string RequireKey(ChatSessionManager manager, ServerStartResult result)
    {
        string key = manager.LocalConnectionKey ?? result.ConnectionKey ?? string.Empty;
        Assert(key == TestPassword, "server did not expose the configured manual password");
        return key;
    }

    private static string ChangeOneKeyCharacter(string key)
    {
        Assert(!string.IsNullOrEmpty(key), "cannot create a wrong password from an empty value");
        return key + " wrong";
    }

    private static bool IsValidManualPassword(string key)
    {
        return key.Length is >= 8 and <= 64
            && key.Trim() == key
            && !string.IsNullOrWhiteSpace(key)
            && Encoding.UTF8.GetByteCount(key) <= 256;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
