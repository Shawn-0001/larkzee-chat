using System.Diagnostics;
using System.Text;
using LarkzeeChat.Controls;
using LarkzeeChat.Models;
using LarkzeeChat.Networking;
using LarkzeeChat.Services;

namespace LarkzeeChat.Forms;

public sealed class MainForm : Form
{
    private const int MaximumInputCharacters = 8_000;
    private const int MaximumInputUtf8Bytes = 48 * 1024;
    private const int RetentionTimerIntervalMilliseconds = 60_000;

    private readonly ChatSessionManager _sessionManager;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly Label _statusLabel;
    private readonly Button _settingsButton;
    private readonly Button _connectionButton;
    private readonly BufferedFlowLayoutPanel _messageList;
    private readonly RichTextBox _messageInput;
    private readonly Button _sendButton;
    private readonly MessageRetentionBuffer _messageHistory = new();
    private readonly System.Windows.Forms.Timer _retentionTimer;

    private bool _isConnecting;
    private bool _isSending;
    private bool _isClosing;
    private bool _allowClose;
    private bool _resizingMessageControls;
    private bool _scrollToLatestPending;
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
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (System.Drawing.Icon)SystemIcons.Application.Clone();
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
        _messageInput.TextChanged += (_, _) => UpdateMessageInputScrollBars();
        _messageInput.Resize += (_, _) => UpdateMessageInputScrollBars();
        _messageList.ViewportLayoutChanged += MessageList_ViewportLayoutChanged;
        Activated += (_, _) => TaskbarNotificationService.Stop(this);

        _retentionTimer = new System.Windows.Forms.Timer
        {
            Interval = RetentionTimerIntervalMilliseconds
        };
        _retentionTimer.Tick += RetentionTimer_Tick;
        _retentionTimer.Start();

        _sessionManager.ConnectionStateChanged += (_, args) =>
        {
            RunOnUi(() => ApplyConnectionState(args.IsConnected));
        };
        _sessionManager.MessageReceived += (_, args) =>
        {
            // The protocol timestamp is display-only. Retention always uses
            // the local receipt time captured by AddMessage.
            string timestamp = args.Timestamp.ToLocalTime().ToString("HH:mm");
            RunOnUi(() =>
            {
                AddMessage(args.Text, timestamp, isOwnMessage: false);
                TaskbarNotificationService.FlashUntilForeground(this);
            });
        };

        ApplyConnectionState(_sessionManager.IsConnected);
        UpdateMessageInputScrollBars();
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
        _retentionTimer.Stop();
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
            _retentionTimer.Stop();
            _retentionTimer.Dispose();
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _messageHistory.Dispose();
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
            Height = 70,
            BackColor = Color.White,
            Padding = new Padding(20, 0, 20, 0),
            AccessibleName = "聊天标题栏"
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = "Larkzee Chat",
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(24, 34, 48), // #182230
            Location = new Point(0, 13),
            AccessibleName = "聊天标题"
        };

        statusLabel = new Label
        {
            AutoSize = true,
            Text = "● 未连接",
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(122, 132, 146), // #7A8492
            Location = new Point(1, 42),
            AccessibleName = "连接状态"
        };

        var configuredSettingsButton = new Button
        {
            AutoSize = false,
            Text = "⚙ 配置",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(98, 109, 124),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Size = new Size(84, 32),
            AccessibleName = "打开配置",
            TabStop = true
        };
        configuredSettingsButton.FlatAppearance.BorderSize = 0;
        configuredSettingsButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 244, 248); // #F1F4F8
        settingsButton = configuredSettingsButton;

        var configuredConnectionButton = new Button
        {
            AutoSize = false,
            Text = "连接",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(24, 119, 210), // #1877D2
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Size = new Size(92, 32),
            AccessibleName = "连接或断开连接",
            TabStop = true
        };
        configuredConnectionButton.FlatAppearance.BorderSize = 0;
        configuredConnectionButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(18, 104, 186); // #1268BA
        connectionButton = configuredConnectionButton;

        var divider = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Color.FromArgb(220, 225, 232), // #DCE1E8
            TabStop = false,
            AccessibleName = "页眉分隔线"
        };

        header.Controls.Add(titleLabel);
        header.Controls.Add(statusLabel);
        header.Controls.Add(settingsButton);
        header.Controls.Add(connectionButton);
        header.Controls.Add(divider);

        void LayoutHeaderButtons()
        {
            int contentBottom = header.ClientSize.Height - divider.Height;
            int top = Math.Max(0, (contentBottom - configuredConnectionButton.Height) / 2);
            configuredConnectionButton.Left = Math.Max(0, header.ClientSize.Width - header.Padding.Right - configuredConnectionButton.Width);
            configuredConnectionButton.Top = top;
            configuredSettingsButton.Left = Math.Max(
                0,
                configuredConnectionButton.Left - 10 - configuredSettingsButton.Width);
            configuredSettingsButton.Top = top;
        }

        header.Resize += (_, _) => LayoutHeaderButtons();
        LayoutHeaderButtons();
        return header;
    }

    private static BufferedFlowLayoutPanel BuildMessageList()
    {
        return new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.FromArgb(247, 248, 250), // #F7F8FA
            BorderStyle = BorderStyle.None,
            TabStop = true,
            AccessibleName = "消息记录"
        };
    }

    private static Panel BuildInputArea(out RichTextBox input, out Button sendButton)
    {
        var area = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 128,
            BackColor = Color.White,
            AccessibleName = "消息编辑区"
        };

        var divider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Color.FromArgb(220, 225, 232), // #DCE1E8
            TabStop = false,
            AccessibleName = "编辑区分隔线"
        };

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(16, 12, 16, 12)
        };

        var inputFrame = new RoundedBorderPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            BorderColor = Color.FromArgb(220, 225, 232), // #DCE1E8
            CornerRadius = 10,
            Padding = new Padding(12, 8, 12, 8),
            AccessibleName = "圆角消息输入框"
        };

        input = new RichTextBox
        {
            Multiline = true,
            AcceptsTab = false,
            DetectUrls = false,
            MaxLength = MaximumInputCharacters,
            ScrollBars = RichTextBoxScrollBars.None,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 30, 30),
            AccessibleName = "消息输入框",
            AccessibleRole = AccessibleRole.Text
        };
        var actionBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            BackColor = Color.White,
            TabStop = false
        };

        var configuredSendButton = new Button
        {
            Text = "发送",
            Size = new Size(72, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(24, 119, 210), // #1877D2
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular, GraphicsUnit.Point),
            AccessibleName = "发送消息",
            AccessibleRole = AccessibleRole.PushButton,
            TabStop = true
        };
        configuredSendButton.FlatAppearance.BorderSize = 0;
        configuredSendButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(18, 104, 186); // #1268BA
        sendButton = configuredSendButton;

        void CenterSendButton()
        {
            configuredSendButton.Left = Math.Max(0, actionBar.ClientSize.Width - configuredSendButton.Width);
            configuredSendButton.Top = Math.Max(0, (actionBar.ClientSize.Height - configuredSendButton.Height) / 2);
        }

        actionBar.Controls.Add(configuredSendButton);
        actionBar.Resize += (_, _) => CenterSendButton();
        inputFrame.Controls.Add(input);
        inputFrame.Controls.Add(actionBar);
        content.Controls.Add(inputFrame);
        area.Controls.Add(content);
        area.Controls.Add(divider);
        CenterSendButton();
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

        if (!TryGetRemoteConnection(out string ip, out string password))
        {
            OpenSettingsForMissingConnection();
            return;
        }

        _isConnecting = true;
        ApplyConnectionState(_sessionManager.IsConnected);

        try
        {
            var result = await _sessionManager.ConnectAsync(ip, password, _lifetimeCts.Token);
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
            ShowFriendlyError("连接失败，请确认对方已开启连接服务，并检查 IP 和密码。");
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
        // Shift+Enter is intentionally left to the multiline RichTextBox.
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

        if (text.Length > MaximumInputCharacters)
        {
            ShowFriendlyError("消息过长，请分段发送。");
            return;
        }

        // Keep ordinary input well below the protocol's 64 KiB framed JSON
        // limit, including room for UTF-8 and JSON envelope overhead.
        if (Encoding.UTF8.GetByteCount(text) > MaximumInputUtf8Bytes)
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
            else if (_sessionManager.IsConnected)
            {
                ShowFriendlyError("消息过长或发送失败，请分段后重试。");
            }
            else
            {
                // A remote close is expected transient state. The connection
                // event updates the status; never interrupt it with a modal.
                ApplyConnectionState(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Application shutdown owns cancellation.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat message send failed: {exception}");
            if (_sessionManager.IsConnected)
            {
                ShowFriendlyError("消息过长或发送失败，请分段后重试。");
            }
            else
            {
                ApplyConnectionState(false);
            }
        }
        finally
        {
            _isSending = false;
            ApplyConnectionState(_sessionManager.IsConnected);
        }
    }

    private bool TryGetRemoteConnection(out string ip, out string password)
    {
        ip = _settings.RemoteIp.Trim();
        password = _settings.RemotePassword;

        if (!Ipv4InputValidation.TryParseDottedDecimal(ip, out _))
        {
            ip = string.Empty;
            password = string.Empty;
            return false;
        }

        if (!AuthenticationService.TryValidateManualPassword(password, out string validatedPassword))
        {
            ip = string.Empty;
            password = string.Empty;
            return false;
        }

        password = validatedPassword;
        return true;
    }

    private void OpenSettingsForMissingConnection()
    {
        ShowFriendlyError("请先在配置中填写对方连接码，或 IP 和密码。");
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
            ConnectFailureReason.AuthenticationFailed => "密码错误。",
            ConnectFailureReason.RateLimited => "认证失败次数过多，请稍后再试。",
            ConnectFailureReason.RemoteBusy => "对方当前已有其他连接。",
            ConnectFailureReason.Cancelled => string.Empty,
            _ => "连接失败，请确认对方已开启连接服务，并检查 IP 和密码。"
        };

        if (!string.IsNullOrEmpty(message))
        {
            ShowFriendlyError(message);
        }
    }

    private void ApplyConnectionState(bool connected)
    {
        _statusLabel.Text = connected ? "● 已连接" : "● 未连接";
        _statusLabel.ForeColor = connected
            ? Color.FromArgb(23, 134, 75) // #17864B
            : Color.FromArgb(122, 132, 146); // #7A8492
        _connectionButton.Text = connected ? "断开" : "连接";
        _connectionButton.BackColor = connected
            ? Color.White
            : Color.FromArgb(24, 119, 210); // #1877D2
        _connectionButton.ForeColor = connected
            ? Color.FromArgb(163, 58, 58) // #A33A3A
            : Color.White;
        _connectionButton.FlatAppearance.BorderSize = connected ? 1 : 0;
        _connectionButton.FlatAppearance.BorderColor = Color.FromArgb(220, 225, 232); // #DCE1E8
        _connectionButton.FlatAppearance.MouseOverBackColor = connected
            ? Color.FromArgb(241, 244, 248) // #F1F4F8
            : Color.FromArgb(18, 104, 186); // #1268BA
        _connectionButton.Enabled = !_isClosing && !_isConnecting;
        _settingsButton.Enabled = !_isClosing && !_isConnecting;
        _sendButton.Enabled = connected && !_isClosing && !_isSending;
        // The composer remains available while disconnected so a draft is not
        // lost when a peer temporarily goes away.
        _messageInput.Enabled = !_isClosing;
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
        bool retained = false;

        _messageList.SuspendLayout();
        try
        {
            _messageList.Controls.Add(messageControl);
            IReadOnlyList<MessageRetentionBuffer.Entry> removed = _messageHistory.Add(
                messageControl,
                text ?? string.Empty,
                DateTimeOffset.Now);
            RemovePrunedMessages(removed);
            retained = !removed.Any(entry => ReferenceEquals(entry.Control, messageControl));
        }
        catch
        {
            if (_messageList.Controls.Contains(messageControl))
            {
                _messageList.Controls.Remove(messageControl);
            }

            messageControl.Dispose();
            throw;
        }
        finally
        {
            _messageList.ResumeLayout(true);
        }

        if (retained && !_messageList.IsDisposed)
        {
            // The scrollbar range may change after the deferred viewport pass
            // gives every row its final width and therefore final height. Wait
            // for that stable pass before scrolling, otherwise the last few
            // pixels of the newest bubble can remain below the viewport.
            _scrollToLatestPending = true;
        }
    }

    private void RetentionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || IsDisposed || Disposing)
        {
            return;
        }

        _messageList.SuspendLayout();
        try
        {
            RemovePrunedMessages(_messageHistory.Prune(DateTimeOffset.Now));
        }
        finally
        {
            _messageList.ResumeLayout(true);
        }
    }

    private void RemovePrunedMessages(IReadOnlyList<MessageRetentionBuffer.Entry> removed)
    {
        foreach (MessageRetentionBuffer.Entry entry in removed)
        {
            if (_messageList.Controls.Contains(entry.Control))
            {
                _messageList.Controls.Remove(entry.Control);
            }

            entry.Control.Dispose();
        }
    }

    private void MessageList_ViewportLayoutChanged(object? sender, EventArgs e)
    {
        bool widthChanged = ResizeMessageControls();
        bool scrollExtentChanged = _messageList.SynchronizeVerticalScrollExtent();
        if (widthChanged || scrollExtentChanged)
        {
            // Width changes can change wrapped row heights; extent changes can
            // in turn create a vertical scrollbar and reduce the viewport.
            // Queue one more coalesced pass so both settle before scrolling.
            _messageList.RequestViewportSynchronization();
            return;
        }

        ScrollToLatestIfPending();
    }

    private bool ResizeMessageControls()
    {
        if (_resizingMessageControls || _messageList.IsDisposed || _messageList.Disposing)
        {
            return false;
        }

        int width = GetMessageControlWidth();
        bool widthChanged = _messageList.Controls.Cast<Control>()
            .Any(control => !control.IsDisposed && control.Width != width);
        if (!widthChanged)
        {
            // BufferedFlowLayoutPanel coalesces layout notifications. Do not
            // create another layout pass when the viewport is already in sync.
            return false;
        }

        _resizingMessageControls = true;
        _messageList.SuspendLayout();
        try
        {
            foreach (Control control in _messageList.Controls)
            {
                if (!control.IsDisposed)
                {
                    if (control.Width != width)
                    {
                        control.Width = width;
                    }
                }
            }
        }
        finally
        {
            _messageList.ResumeLayout(true);
            _resizingMessageControls = false;
        }

        return true;
    }

    private void ScrollToLatestIfPending()
    {
        if (!_scrollToLatestPending || _messageList.IsDisposed || _messageList.Disposing)
        {
            return;
        }

        ChatMessageControl? latest = _messageList.Controls
            .OfType<ChatMessageControl>()
            .LastOrDefault(control => !control.IsDisposed);
        _scrollToLatestPending = false;
        if (latest is not null)
        {
            _messageList.ScrollControlIntoView(latest);
        }
    }

    private int GetMessageControlWidth()
    {
        return Math.Max(
            1,
            _messageList.ClientSize.Width - _messageList.Padding.Horizontal);
    }

    private void UpdateMessageInputScrollBars()
    {
        if (_messageInput.IsDisposed || _messageInput.ClientSize.Height <= 0)
        {
            return;
        }

        bool needsScroll = false;
        if (_messageInput.TextLength > 0)
        {
            try
            {
                Point textEnd = _messageInput.GetPositionFromCharIndex(_messageInput.TextLength);
                needsScroll = textEnd.Y + _messageInput.Font.Height + 2 > _messageInput.ClientSize.Height;
            }
            catch (ArgumentOutOfRangeException)
            {
                needsScroll = false;
            }
        }

        RichTextBoxScrollBars desired = needsScroll
            ? RichTextBoxScrollBars.Vertical
            : RichTextBoxScrollBars.None;
        if (_messageInput.ScrollBars != desired)
        {
            _messageInput.ScrollBars = desired;
        }
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || Disposing || _isClosing)
        {
            return;
        }

        void RunIfAlive()
        {
            // A background event can queue this callback immediately before
            // closing starts. Recheck on the UI thread so no message/layout
            // work runs against controls that are being torn down.
            if (!IsDisposed && !Disposing && !_isClosing)
            {
                action();
            }
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(RunIfAlive);
            }
            else
            {
                RunIfAlive();
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

    private static void ShowFriendlyError(string message)
    {
        MessageBox.Show(message, "Larkzee Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
