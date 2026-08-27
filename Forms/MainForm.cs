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
    private readonly EmojiPackService _emojiPackService;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly Label _statusLabel;
    private readonly Button _settingsButton;
    private readonly Button _connectionButton;
    private readonly BufferedFlowLayoutPanel _messageList;
    private readonly RichTextBox _messageInput;
    private readonly Button _emojiButton;
    private readonly Button _imageButton;
    private readonly Button _fileButton;
    private readonly Button _sendButton;
    private readonly MessageRetentionBuffer _messageHistory = new();
    private readonly Dictionary<string, AttachmentMessageControl> _attachmentControls =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StickerMessageControl> _stickerControls =
        new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer _retentionTimer;
    private EmojiPickerForm? _emojiPicker;

    private bool _isConnecting;
    private bool _isSending;
    private bool _isSendingAttachment;
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
        _emojiPackService = new EmojiPackService();

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
        var inputArea = BuildInputArea(
            out _messageInput,
            out _emojiButton,
            out _imageButton,
            out _fileButton,
            out _sendButton);

        Controls.Add(_messageList);
        Controls.Add(inputArea);
        Controls.Add(header);

        _settingsButton.Click += SettingsButton_Click;
        _connectionButton.Click += ConnectionButton_Click;
        _emojiButton.Click += EmojiButton_Click;
        _imageButton.Click += ImageButton_Click;
        _fileButton.Click += FileButton_Click;
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
        _sessionManager.AttachmentOffered += (_, args) =>
        {
            RunOnUi(() => HandleIncomingAttachmentOffer(args));
        };
        _sessionManager.AttachmentTransferStarted += (_, args) =>
        {
            RunOnUi(() =>
            {
                if (args.IsSticker || args.IsInlineImage)
                {
                    AddStickerControl(args);
                }
                else
                {
                    AddAttachmentControl(args);
                }
            });
        };
        _sessionManager.AttachmentTransferProgressChanged += (_, args) =>
        {
            RunOnUi(() =>
            {
                if (args.IsSticker || args.IsInlineImage)
                {
                    UpdateStickerProgress(args);
                }
                else
                {
                    UpdateAttachmentProgress(args);
                }
            });
        };
        _sessionManager.AttachmentTransferCompleted += (_, args) =>
        {
            RunOnUi(() =>
            {
                if (args.IsSticker || args.IsInlineImage)
                {
                    CompleteSticker(args);
                }
                else
                {
                    CompleteAttachment(args);
                }
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
        _emojiPicker?.Close();
        _emojiPicker = null;
        _retentionTimer.Stop();
        _settingsButton.Enabled = false;
        _connectionButton.Enabled = false;
        _emojiButton.Enabled = false;
        _imageButton.Enabled = false;
        _fileButton.Enabled = false;
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
            _emojiPicker?.Dispose();
            _emojiPicker = null;
            _attachmentControls.Clear();
            _stickerControls.Clear();
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
            Height = 52,
            BackColor = Color.White,
            Padding = new Padding(16, 0, 16, 0),
            AccessibleName = "连接工具栏"
        };

        var statusPill = new RoundedBorderPanel
        {
            BackColor = Color.FromArgb(241, 244, 248), // #F1F4F8
            BorderColor = Color.FromArgb(220, 225, 232), // #DCE1E8
            CornerRadius = 14,
            Size = new Size(84, 28),
            TabStop = false,
            AccessibleName = "连接状态胶囊"
        };

        statusLabel = new Label
        {
            AutoSize = false,
            Text = "● 未连接",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(122, 132, 146), // #7A8492
            AccessibleName = "连接状态"
        };
        statusPill.Controls.Add(statusLabel);

        var configuredSettingsButton = new Button
        {
            AutoSize = false,
            Text = "⚙ 配置",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(98, 109, 124),
            Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            Size = new Size(72, 28),
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
            Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            Size = new Size(78, 28),
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

        header.Controls.Add(statusPill);
        header.Controls.Add(settingsButton);
        header.Controls.Add(connectionButton);
        header.Controls.Add(divider);

        void LayoutHeaderButtons()
        {
            int contentBottom = header.ClientSize.Height - divider.Height;
            int top = Math.Max(0, (contentBottom - configuredConnectionButton.Height) / 2);
            statusPill.Left = header.Padding.Left;
            statusPill.Top = Math.Max(0, (contentBottom - statusPill.Height) / 2);
            configuredConnectionButton.Left = Math.Max(0, header.ClientSize.Width - header.Padding.Right - configuredConnectionButton.Width);
            configuredConnectionButton.Top = top;
            configuredSettingsButton.Left = Math.Max(
                0,
                configuredConnectionButton.Left - 6 - configuredSettingsButton.Width);
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

    private static Panel BuildInputArea(
        out RichTextBox input,
        out Button emojiButton,
        out Button imageButton,
        out Button fileButton,
        out Button sendButton)
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

        Button BuildAttachmentButton(string text, string accessibleName)
        {
            var button = new Button
            {
                Text = text,
                Size = new Size(54, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(98, 109, 124),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
                AccessibleName = accessibleName,
                AccessibleRole = AccessibleRole.PushButton,
                TabStop = true
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 244, 248);
            return button;
        }

        Button configuredEmojiButton = BuildAttachmentButton("表情", "选择表情");
        Button configuredImageButton = BuildAttachmentButton("图片", "发送图片");
        Button configuredFileButton = BuildAttachmentButton("文件", "发送文件");
        emojiButton = configuredEmojiButton;
        imageButton = configuredImageButton;
        fileButton = configuredFileButton;

        void LayoutActionButtons()
        {
            configuredSendButton.Left = Math.Max(0, actionBar.ClientSize.Width - configuredSendButton.Width);
            configuredSendButton.Top = Math.Max(0, (actionBar.ClientSize.Height - configuredSendButton.Height) / 2);
            configuredEmojiButton.Location = new Point(0, configuredSendButton.Top);
            configuredImageButton.Location = new Point(58, configuredSendButton.Top);
            configuredFileButton.Location = new Point(116, configuredSendButton.Top);
        }

        actionBar.Controls.Add(configuredEmojiButton);
        actionBar.Controls.Add(configuredImageButton);
        actionBar.Controls.Add(configuredFileButton);
        actionBar.Controls.Add(configuredSendButton);
        actionBar.Resize += (_, _) => LayoutActionButtons();
        inputFrame.Controls.Add(input);
        inputFrame.Controls.Add(actionBar);
        content.Controls.Add(inputFrame);
        area.Controls.Add(content);
        area.Controls.Add(divider);
        LayoutActionButtons();
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

    private void EmojiButton_Click(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (_emojiPicker is { IsDisposed: false })
        {
            _emojiPicker.Activate();
            return;
        }

        var picker = new EmojiPickerForm(_emojiPackService);
        _emojiPicker = picker;
        picker.SelectionMade += EmojiPicker_SelectionMade;
        picker.FormClosed += EmojiPicker_FormClosed;
        picker.Show(this);
    }

    private void EmojiPicker_SelectionMade(object? sender, EventArgs e)
    {
        if (sender is not EmojiPickerForm picker)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(picker.SelectedStickerPath))
        {
            if (!_sessionManager.IsConnected)
            {
                ShowFriendlyError("请先连接对方，再发送自定义表情。");
                return;
            }

            _ = SendStickerFromPathAsync(picker.SelectedStickerPath);
            return;
        }

        if (string.IsNullOrEmpty(picker.SelectedEmoji)
            || _messageInput.TextLength + picker.SelectedEmoji.Length > MaximumInputCharacters)
        {
            return;
        }

        _messageInput.SelectedText = picker.SelectedEmoji;
        _messageInput.Focus();
    }

    private void EmojiPicker_FormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not EmojiPickerForm picker)
        {
            return;
        }

        picker.SelectionMade -= EmojiPicker_SelectionMade;
        picker.FormClosed -= EmojiPicker_FormClosed;
        if (ReferenceEquals(_emojiPicker, picker))
        {
            _emojiPicker = null;
        }

        picker.Dispose();
    }

    private async Task SendStickerFromPathAsync(string path)
    {
        if (_isClosing || _isSendingAttachment)
        {
            return;
        }

        if (!_sessionManager.IsConnected)
        {
            ShowFriendlyError("请先连接对方，再发送自定义表情。");
            return;
        }

        _isSendingAttachment = true;
        ApplyConnectionState(_sessionManager.IsConnected);
        try
        {
            AttachmentSendResult result = await _sessionManager.SendStickerAsync(
                path,
                _lifetimeCts.Token);
            if (!result.Succeeded && result.TransferId is null && !_isClosing)
            {
                ShowFriendlyError(result.Message);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Closing owns cancellation.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat sticker send failed: {exception}");
            if (!_isClosing)
            {
                ShowFriendlyError("表情发送失败，请重试。");
            }
        }
        finally
        {
            _isSendingAttachment = false;
            ApplyConnectionState(_sessionManager.IsConnected);
        }
    }

    private void ImageButton_Click(object? sender, EventArgs e)
    {
        if (!CanStartAttachmentSend())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择要发送的图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            string? contentType = GetImageContentType(dialog.FileName);
            if (contentType is null || !ChatSessionManager.IsInlineImageContentType(contentType))
            {
                ShowFriendlyError("请选择 PNG、JPEG、GIF、BMP 或 WebP 图片。\n当前文件格式不支持聊天内预览。");
                return;
            }

            _ = SendImageFromPathAsync(dialog.FileName, contentType);
        }
    }

    private void FileButton_Click(object? sender, EventArgs e)
    {
        if (!CanStartAttachmentSend())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择要发送的文件",
            Filter = "所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ = SendAttachmentFromPathAsync(dialog.FileName, null);
        }
    }

    private bool CanStartAttachmentSend()
    {
        if (_isClosing || _isSendingAttachment)
        {
            return false;
        }

        if (!_sessionManager.IsConnected)
        {
            ShowFriendlyError("请先连接对方，再发送图片或文件。");
            return false;
        }

        return true;
    }

    private async Task SendAttachmentFromPathAsync(string path, string? contentType)
    {
        _isSendingAttachment = true;
        ApplyConnectionState(_sessionManager.IsConnected);
        try
        {
            AttachmentSendResult result = await _sessionManager.SendAttachmentAsync(
                path,
                contentType,
                _lifetimeCts.Token);
            if (!result.Succeeded && result.TransferId is null && !_isClosing)
            {
                ShowFriendlyError(result.Message);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Closing owns cancellation.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat attachment send failed: {exception}");
            if (!_isClosing)
            {
                ShowFriendlyError("附件发送失败，请重试。");
            }
        }
        finally
        {
            _isSendingAttachment = false;
            ApplyConnectionState(_sessionManager.IsConnected);
        }
    }

    private async Task SendImageFromPathAsync(string path, string contentType)
    {
        _isSendingAttachment = true;
        ApplyConnectionState(_sessionManager.IsConnected);
        try
        {
            AttachmentSendResult result = await _sessionManager.SendImageAsync(
                path,
                contentType,
                _lifetimeCts.Token);
            if (!result.Succeeded && result.TransferId is null && !_isClosing)
            {
                ShowFriendlyError(result.Message);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Closing owns cancellation.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat image send failed: {exception}");
            if (!_isClosing)
            {
                ShowFriendlyError("图片发送失败，请重试。");
            }
        }
        finally
        {
            _isSendingAttachment = false;
            ApplyConnectionState(_sessionManager.IsConnected);
        }
    }

    private async void HandleIncomingAttachmentOffer(IncomingAttachmentOfferEventArgs args)
    {
        if (args.IsSticker || args.IsInlineImage)
        {
            try
            {
                bool inlineAccepted = args.IsInlineImage
                    ? await _sessionManager.AcceptIncomingImageAsync(
                        args.TransferId,
                        _lifetimeCts.Token)
                    : await _sessionManager.AcceptIncomingStickerAsync(
                        args.TransferId,
                        _lifetimeCts.Token);
                if (!inlineAccepted && !_isClosing)
                {
                    ShowFriendlyError(args.IsInlineImage
                        ? "无法接收图片，请让对方重试。"
                        : "无法接收自定义表情，请让对方重试。");
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // Closing owns cancellation.
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Larkzee Chat inline media receive failed: {exception}");
                if (!_isClosing)
                {
                    ShowFriendlyError(args.IsInlineImage
                        ? "图片接收失败，请让对方重试。"
                        : "自定义表情接收失败，请让对方重试。");
                }
            }

            return;
        }

        AttachmentMessageControl control = EnsureAttachmentControl(
            args.TransferId,
            args.FileName,
            args.ContentType,
            args.FileSize,
            isOwnMessage: false,
            args.Timestamp.ToLocalTime().ToString("HH:mm"));
        control.UpdateProgress(0, args.FileSize, AttachmentTransferStage.WaitingForPeer);
        TaskbarNotificationService.FlashUntilForeground(this);

        using var dialog = new SaveFileDialog
        {
            Title = $"接收文件：{args.FileName}",
            FileName = args.FileName,
            Filter = args.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*"
                : "所有文件|*.*",
            AddExtension = false,
            CheckPathExists = true,
            OverwritePrompt = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            await _sessionManager.RejectIncomingAttachmentAsync(
                args.TransferId,
                "已取消接收。",
                _lifetimeCts.Token);
            return;
        }

        bool accepted = await _sessionManager.AcceptIncomingAttachmentAsync(
            args.TransferId,
            dialog.FileName,
            _lifetimeCts.Token);
        if (!accepted
            && !_isClosing
            && control.Stage is not AttachmentTransferStage.Cancelled
                and not AttachmentTransferStage.Rejected
                and not AttachmentTransferStage.Failed)
        {
            await _sessionManager.RejectIncomingAttachmentAsync(
                args.TransferId,
                "无法写入所选位置。",
                _lifetimeCts.Token);
            ShowFriendlyError("无法在所选位置创建文件，请重新发送后选择其他位置。");
        }
    }

    private void AddAttachmentControl(AttachmentTransferStartedEventArgs args)
    {
        AttachmentMessageControl control = EnsureAttachmentControl(
            args.TransferId,
            args.FileName,
            args.ContentType,
            args.FileSize,
            isOwnMessage: !args.IsIncoming,
            DateTimeOffset.Now.ToString("HH:mm"));
        if (!args.IsIncoming && File.Exists(args.LocalPath))
        {
            control.ShowLocalPreview(args.LocalPath);
        }
    }

    private void AddStickerControl(AttachmentTransferStartedEventArgs args)
    {
        if (!args.IsSticker && !args.IsInlineImage)
        {
            return;
        }

        StickerMessageControl control = EnsureStickerControl(
            args.TransferId,
            args.FileName,
            args.ContentType,
            isOwnMessage: !args.IsIncoming,
            DateTimeOffset.Now.ToString("HH:mm"),
            isInlineImage: args.IsInlineImage);
        if (!args.IsIncoming && !string.IsNullOrWhiteSpace(args.LocalPath))
        {
            control.ShowLocalPreview(args.LocalPath);
        }
    }

    private AttachmentMessageControl EnsureAttachmentControl(
        string transferId,
        string fileName,
        string contentType,
        long fileSize,
        bool isOwnMessage,
        string timestamp)
    {
        if (_attachmentControls.TryGetValue(transferId, out AttachmentMessageControl? existing)
            && !existing.IsDisposed)
        {
            return existing;
        }

        var control = new AttachmentMessageControl(
            transferId,
            fileName,
            contentType,
            fileSize,
            isOwnMessage,
            timestamp)
        {
            Width = GetMessageControlWidth()
        };
        control.OpenFolderRequested += (_, args) => OpenAttachmentFolder(args.Path);
        _attachmentControls[transferId] = control;
        _messageList.SuspendLayout();
        try
        {
            _messageList.Controls.Add(control);
            RemovePrunedMessages(_messageHistory.Add(
                control,
                fileName,
                DateTimeOffset.Now));
        }
        finally
        {
            _messageList.ResumeLayout(true);
        }

        _scrollToLatestPending = true;
        return control;
    }

    private void UpdateAttachmentProgress(AttachmentTransferProgressEventArgs args)
    {
        if (_attachmentControls.TryGetValue(args.TransferId, out AttachmentMessageControl? control)
            && !control.IsDisposed)
        {
            control.UpdateProgress(args.BytesTransferred, args.TotalBytes, args.Stage);
        }
    }

    private void UpdateStickerProgress(AttachmentTransferProgressEventArgs args)
    {
        if (_stickerControls.TryGetValue(args.TransferId, out StickerMessageControl? control)
            && !control.IsDisposed)
        {
            control.UpdateProgress(args.BytesTransferred, args.TotalBytes, args.Stage);
        }
    }

    private void CompleteAttachment(AttachmentTransferCompletedEventArgs args)
    {
        if (_attachmentControls.TryGetValue(args.TransferId, out AttachmentMessageControl? control)
            && !control.IsDisposed)
        {
            control.Complete(args.Succeeded, args.Stage, args.Message, args.LocalPath);
            _messageList.RequestViewportSynchronization();
            _scrollToLatestPending = true;
        }

        if (args.IsIncoming)
        {
            TaskbarNotificationService.FlashUntilForeground(this);
        }
    }

    private void CompleteSticker(AttachmentTransferCompletedEventArgs args)
    {
        if (!args.IsSticker && !args.IsInlineImage)
        {
            return;
        }

        if (_stickerControls.TryGetValue(args.TransferId, out StickerMessageControl? control)
            && !control.IsDisposed)
        {
            control.Complete(
                args.Succeeded,
                args.Stage,
                args.Message,
                args.LocalPath,
                args.ContentBytes);
            _messageList.RequestViewportSynchronization();
            _scrollToLatestPending = true;
        }

        if (args.IsIncoming)
        {
            TaskbarNotificationService.FlashUntilForeground(this);
        }
    }

    private StickerMessageControl EnsureStickerControl(
        string transferId,
        string fileName,
        string contentType,
        bool isOwnMessage,
        string timestamp,
        bool isInlineImage)
    {
        if (_stickerControls.TryGetValue(transferId, out StickerMessageControl? existing)
            && !existing.IsDisposed)
        {
            return existing;
        }

        var control = new StickerMessageControl(
            transferId,
            fileName,
            contentType,
            isOwnMessage,
            timestamp,
            isInlineImage)
        {
            Width = GetMessageControlWidth()
        };
        _stickerControls[transferId] = control;
        _messageList.SuspendLayout();
        try
        {
            _messageList.Controls.Add(control);
            RemovePrunedMessages(_messageHistory.Add(
                control,
                fileName,
                DateTimeOffset.Now));
        }
        finally
        {
            _messageList.ResumeLayout(true);
        }

        _scrollToLatestPending = true;
        return control;
    }

    private static void OpenAttachmentFolder(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(fullPath);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or IOException
                                           or System.ComponentModel.Win32Exception
                                           or ArgumentException)
        {
            Debug.WriteLine(exception);
            ShowFriendlyError("无法打开文件所在位置。");
        }
    }

    private static string? GetImageContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => null
        };
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
        if (_statusLabel.Parent is RoundedBorderPanel statusPill)
        {
            statusPill.BackColor = connected
                ? Color.FromArgb(236, 248, 241) // #ECF8F1
                : Color.FromArgb(241, 244, 248); // #F1F4F8
            statusPill.BorderColor = connected
                ? Color.FromArgb(201, 233, 214) // #C9E9D6
                : Color.FromArgb(220, 225, 232); // #DCE1E8
        }
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
        _emojiButton.Enabled = !_isClosing;
        _imageButton.Enabled = connected && !_isClosing && !_isSendingAttachment;
        _fileButton.Enabled = connected && !_isClosing && !_isSendingAttachment;
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
            if (entry.Control is AttachmentMessageControl attachment)
            {
                _attachmentControls.Remove(attachment.TransferId);
            }
            else if (entry.Control is StickerMessageControl sticker)
            {
                _stickerControls.Remove(sticker.TransferId);
            }

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

        Control? latest = _messageList.Controls
            .Cast<Control>()
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
