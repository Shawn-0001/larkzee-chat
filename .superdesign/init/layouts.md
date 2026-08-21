# Shared layouts

This desktop app has no web layout/router layer. `MainForm` is the single persistent application shell: a fixed top header, fill-docked scrolling conversation surface, and bottom composer. Its private `BuildHeader`, `BuildMessageList`, and `BuildInputArea` methods are the real shared layout implementation used for the entire main experience.

## MainForm application shell

- File: `Forms/MainForm.cs`
- Renders: product title and connection state; configuration/connection actions; scrollable message feed; multiline composer and send action.
- Window sizing: 720x620 client area, 560x450 minimum, centered on screen.
- Layout behavior: WinForms docking and anchors; each message row is resized to the message viewport and each bubble self-aligns.

### Full source

```csharp
using System.Diagnostics;
using System.Text;
using LarkzeeChat.Controls;
using LarkzeeChat.Models;
using LarkzeeChat.Networking;
using LarkzeeChat.Services;

namespace LarkzeeChat.Forms;

public sealed class MainForm : Form
{
    private readonly ChatSessionManager _sessionManager;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly Label _statusLabel;
    private readonly Button _settingsButton;
    private readonly Button _connectionButton;
    private readonly FlowLayoutPanel _messageList;
    private readonly TextBox _messageInput;
    private readonly Button _sendButton;

    private bool _isConnecting;
    private bool _isSending;
    private bool _isClosing;
    private bool _allowClose;
    private bool _isConnected;
    private int _disposeStarted;

    public MainForm(
        ChatSessionManager sessionManager,
        SettingsService settingsService,
        AppSettings settings)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        Text = "Larkzee Chat";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 620);
        MinimumSize = new Size(560, 450);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;

        var header = BuildHeader(out _statusLabel, out _settingsButton, out _connectionButton);
        _messageList = BuildMessageList();
        var inputArea = BuildInputArea(out _messageInput, out _sendButton);

        Controls.Add(_messageList);
        Controls.Add(inputArea);
        Controls.Add(header);

        _settingsButton.Click += SettingsButton_Click;
        _connectionButton.Click += ConnectionButton_Click;
        _sendButton.Click += SendButton_Click;
        _messageInput.KeyDown += MessageInput_KeyDown;
        _messageList.ClientSizeChanged += (_, _) => ResizeMessageControls();

        _sessionManager.ConnectionStateChanged += (_, args) =>
        {
            RunOnUi(() =>
            {
                bool wasConnected = _isConnected;
                ApplyConnectionState(args.IsConnected);
                if (wasConnected
                    && !args.IsConnected
                    && args.Reason is ConnectionClosedReason.RemoteRequest or ConnectionClosedReason.ConnectionLost)
                {
                    ShowFriendlyError("连接已断开。");
                }
            });
        };
        _sessionManager.MessageReceived += (_, args) =>
        {
            string timestamp = args.Timestamp.ToLocalTime().ToString("HH:mm");
            RunOnUi(() => AddMessage(args.Text, timestamp, isOwnMessage: false));
        };

        ApplyConnectionState(_sessionManager.IsConnected);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _settingsButton.Enabled = false;
        _connectionButton.Enabled = false;
        _sendButton.Enabled = false;
        _messageInput.Enabled = false;

        _ = FinishClosingAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Panel BuildHeader(
        out Label statusLabel,
        out Button settingsButton,
        out Button connectionButton)
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 66,
            BackColor = Color.White,
            Padding = new Padding(18, 12, 18, 8)
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = "Larkzee Chat",
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(35, 35, 35),
            Location = new Point(18, 14)
        };

        statusLabel = new Label
        {
            AutoSize = true,
            Text = "● 未连接",
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(110, 110, 110),
            Location = new Point(20, 39)
        };

        settingsButton = new Button
        {
            AutoSize = true,
            Text = "⚙ 配置",
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            MinimumSize = new Size(86, 30),
            Location = new Point(header.ClientSize.Width - 194, 15)
        };

        connectionButton = new Button
        {
            AutoSize = true,
            Text = "连接",
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            MinimumSize = new Size(86, 30),
            Location = new Point(header.ClientSize.Width - 96, 15)
        };

        header.Controls.Add(titleLabel);
        header.Controls.Add(statusLabel);
        header.Controls.Add(settingsButton);
        header.Controls.Add(connectionButton);
        Button layoutSettingsButton = settingsButton;
        Button layoutConnectionButton = connectionButton;
        header.Resize += (_, _) =>
        {
            layoutConnectionButton.Left = header.ClientSize.Width - layoutConnectionButton.Width;
            layoutSettingsButton.Left = layoutConnectionButton.Left - layoutSettingsButton.Width - 10;
        };

        return header;
    }

    private static FlowLayoutPanel BuildMessageList()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.None,
            TabStop = true
        };
    }

    private static Panel BuildInputArea(out TextBox input, out Button sendButton)
    {
        var area = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 112,
            BackColor = Color.White,
            Padding = new Padding(12, 10, 12, 12)
        };

        input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            MaxLength = 8_000,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 30, 30),
            AccessibleName = "消息输入框"
        };

        sendButton = new Button
        {
            Text = "发送",
            Dock = DockStyle.Right,
            Width = 84,
            Margin = new Padding(10, 0, 0, 0),
            FlatStyle = FlatStyle.System,
            AccessibleName = "发送消息"
        };

        area.Controls.Add(input);
        area.Controls.Add(sendButton);
        return area;
    }

    private void SettingsButton_Click(object? sender, EventArgs e)
    {
        if (_isClosing || _isConnecting)
        {
            return;
        }

        using var settingsForm = new SettingsForm(_sessionManager, _settingsService, _settings);
        settingsForm.ShowDialog(this);
        ApplyConnectionState(_sessionManager.IsConnected);
    }

    private async void ConnectionButton_Click(object? sender, EventArgs e)
    {
        if (_isClosing || _isConnecting)
        {
            return;
        }

        if (_sessionManager.IsConnected)
        {
            try
            {
                await _sessionManager.DisconnectAsync(ConnectionClosedReason.LocalRequest);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Larkzee Chat disconnect failed: {exception}");
                ShowFriendlyError("断开连接失败，请稍后重试。");
            }

            return;
        }

        if (!TryGetRemoteConnection(out string ip, out string key))
        {
            OpenSettingsForMissingConnection();
            return;
        }

        _isConnecting = true;
        ApplyConnectionState(_sessionManager.IsConnected);

        try
        {
            var result = await _sessionManager.ConnectAsync(ip, key, _lifetimeCts.Token);
            if (!result.Succeeded && result.FailureReason != ConnectFailureReason.Cancelled)
            {
                ShowConnectFailure(result.FailureReason);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown owns cancellation and does not need a dialog.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat connect failed: {exception}");
            ShowFriendlyError("连接失败，请确认对方已开启连接服务，并检查 IP 和连接密钥。");
        }
        finally
        {
            _isConnecting = false;
            ApplyConnectionState(_sessionManager.IsConnected);
        }
    }

    private void SendButton_Click(object? sender, EventArgs e)
    {
        _ = SendCurrentMessageAsync();
    }

    private void MessageInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            _ = SendCurrentMessageAsync();
        }
        // Shift+Enter is intentionally left to the multiline TextBox.
    }

    private async Task SendCurrentMessageAsync()
    {
        if (_isClosing || _isSending || !_sessionManager.IsConnected)
        {
            return;
        }

        string text = _messageInput.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Keep ordinary input well below the protocol's 64 KiB framed JSON
        // limit, including room for UTF-8 and JSON envelope overhead.
        if (Encoding.UTF8.GetByteCount(text) > 48 * 1024)
        {
            ShowFriendlyError("消息过长，请分段发送。");
            return;
        }

        _isSending = true;
        _sendButton.Enabled = false;

        try
        {
            bool sent = await _sessionManager.SendMessageAsync(text, _lifetimeCts.Token);
            if (sent)
            {
                AddMessage(text, DateTimeOffset.Now.ToString("HH:mm"), isOwnMessage: true);
                _messageInput.Clear();
            }
            else
            {
                ShowFriendlyError(_sessionManager.IsConnected
                    ? "消息过长或发送失败，请分段后重试。"
                    : "消息发送失败，连接已断开。");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown owns cancellation.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat message send failed: {exception}");
            ShowFriendlyError(_sessionManager.IsConnected
                ? "消息过长或发送失败，请分段后重试。"
                : "消息发送失败，连接已断开。");
        }
        finally
        {
            _isSending = false;
            ApplyConnectionState(_sessionManager.IsConnected);
        }
    }

    private bool TryGetRemoteConnection(out string ip, out string key)
    {
        ip = _settings.RemoteIp.Trim();
        key = _settings.RemoteKey;

        if (!Ipv4InputValidation.TryParseDottedDecimal(ip, out _))
        {
            ip = string.Empty;
            key = string.Empty;
            return false;
        }

        if (!IsConnectionKey(key))
        {
            ip = string.Empty;
            key = string.Empty;
            return false;
        }

        return true;
    }

    private void OpenSettingsForMissingConnection()
    {
        ShowFriendlyError("请先在配置中填写对方 IP 和连接密钥。");
        using var settingsForm = new SettingsForm(_sessionManager, _settingsService, _settings);
        settingsForm.ShowDialog(this);
        ApplyConnectionState(_sessionManager.IsConnected);
    }

    private void ShowConnectFailure(ConnectFailureReason reason)
    {
        string message = reason switch
        {
            ConnectFailureReason.AlreadyConnected => "当前已经存在连接。",
            ConnectFailureReason.InvalidAddress => "对方 IP 地址无效。",
            ConnectFailureReason.AuthenticationFailed => "连接密钥错误。",
            ConnectFailureReason.RateLimited => "认证失败次数过多，请稍后再试。",
            ConnectFailureReason.RemoteBusy => "对方当前已有其他连接。",
            ConnectFailureReason.Cancelled => string.Empty,
            _ => "连接失败，请确认对方已开启连接服务，并检查 IP 和连接密钥。"
        };

        if (!string.IsNullOrEmpty(message))
        {
            ShowFriendlyError(message);
        }
    }

    private void ApplyConnectionState(bool connected)
    {
        _isConnected = connected;
        _statusLabel.Text = connected ? "● 已连接" : "● 未连接";
        _statusLabel.ForeColor = connected
            ? Color.FromArgb(30, 130, 70)
            : Color.FromArgb(110, 110, 110);
        _connectionButton.Text = connected ? "断开" : "连接";
        _connectionButton.Enabled = !_isClosing && !_isConnecting;
        _settingsButton.Enabled = !_isClosing && !_isConnecting;
        _sendButton.Enabled = connected && !_isClosing && !_isSending;
    }

    private void AddMessage(string text, string timestamp, bool isOwnMessage)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        var messageControl = new ChatMessageControl(text, timestamp, isOwnMessage)
        {
            Width = GetMessageControlWidth()
        };
        _messageList.Controls.Add(messageControl);
        ResizeMessageControls();
        _messageList.ScrollControlIntoView(messageControl);
    }

    private void ResizeMessageControls()
    {
        int width = GetMessageControlWidth();
        foreach (Control control in _messageList.Controls)
        {
            control.Width = width;
        }
    }

    private int GetMessageControlWidth()
    {
        return Math.Max(100, _messageList.ClientSize.Width - _messageList.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || Disposing || _isClosing)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (InvalidOperationException)
        {
            // The window may be between disposal and handle teardown.
        }
    }

    private async Task FinishClosingAsync()
    {
        try
        {
            await _sessionManager.DisposeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat session cleanup failed: {exception}");
        }

        _settingsService.Save(_settings);
        _allowClose = true;

        try
        {
            BeginInvoke(new Action(Close));
        }
        catch (InvalidOperationException)
        {
            // The handle may already be gone; disposal is still complete.
        }
    }

    private static bool IsConnectionKey(string? key)
    {
        if (key is null || key.Length != 6)
        {
            return false;
        }

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%&*?";
        return key.All(alphabet.Contains);
    }

    private static void ShowFriendlyError(string message)
    {
        MessageBox.Show(message, "Larkzee Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
```
