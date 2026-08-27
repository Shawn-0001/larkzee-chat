using LarkzeeChat.Networking;

namespace LarkzeeChat.Controls;

/// <summary>
/// An in-memory image/sticker message. The source file is read into a private
/// byte buffer, and verified incoming bytes are copied from the transfer event
/// args. No received image is written to the cache or a destination file.
/// </summary>
public sealed class StickerMessageControl : UserControl
{
    private const int StickerMaximumBubbleWidth = 292;
    private const int ImageMaximumBubbleWidth = 420;
    private const int MinimumBubbleWidth = 180;
    private const int BubblePadding = 10;
    private const int StickerPreviewMaximumWidth = 248;
    private const int ImagePreviewMaximumWidth = 376;
    private const int StickerPreviewMaximumHeight = 190;
    private const int ImagePreviewMaximumHeight = 260;

    private readonly RoundedBorderPanel _bubble;
    private readonly PictureBox _preview;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _timeLabel;
    private readonly ContextMenuStrip _saveMenu;
    private readonly bool _isOwnMessage;
    private readonly bool _isInlineImage;

    private Image? _image;
    private MemoryStream? _imageStream;
    private byte[]? _imageBytes;
    private bool _previewFailure;

    public StickerMessageControl(
        string transferId,
        string fileName,
        string contentType,
        bool isOwnMessage,
        string timestamp,
        bool isInlineImage = false)
    {
        TransferId = transferId ?? throw new ArgumentNullException(nameof(transferId));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        _isOwnMessage = isOwnMessage;
        _isInlineImage = isInlineImage;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        Margin = new Padding(0, 0, 0, 8);
        BackColor = Color.FromArgb(247, 248, 250);
        TabStop = false;

        _bubble = new RoundedBorderPanel
        {
            BackColor = isOwnMessage ? Color.FromArgb(220, 238, 255) : Color.White,
            BorderColor = isOwnMessage
                ? Color.FromArgb(199, 225, 250)
                : Color.FromArgb(225, 229, 234),
            CornerRadius = 11,
            TabStop = false,
            AccessibleName = _isInlineImage ? "图片消息气泡" : "表情消息气泡"
        };
        _preview = new PictureBox
        {
            BackColor = Color.Transparent,
            BorderStyle = BorderStyle.None,
            SizeMode = PictureBoxSizeMode.Zoom,
            Visible = false,
            AccessibleName = _isInlineImage ? "图片预览" : "表情预览"
        };
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            TabStop = false,
            AccessibleName = _isInlineImage ? "图片传输进度" : "表情传输进度"
        };
        _statusLabel = new Label
        {
            AutoSize = false,
            Text = _isInlineImage ? "正在准备图片…" : "正在准备表情…",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(98, 109, 124),
            AutoEllipsis = true,
            UseMnemonic = false,
            AccessibleName = _isInlineImage ? "图片传输状态" : "表情传输状态"
        };
        _timeLabel = new Label
        {
            AutoSize = false,
            Text = timestamp,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(122, 132, 146),
            TextAlign = ContentAlignment.MiddleRight,
            UseMnemonic = false,
            AccessibleName = _isInlineImage ? "图片消息时间" : "表情消息时间"
        };

        _saveMenu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            AccessibleName = _isInlineImage ? "图片操作" : "表情操作"
        };
        var saveItem = new ToolStripMenuItem("另存为…")
        {
            AccessibleName = _isInlineImage ? "另存为图片" : "另存为表情"
        };
        saveItem.Click += (_, _) => SaveAs();
        _saveMenu.Items.Add(saveItem);
        _preview.ContextMenuStrip = _saveMenu;
        _bubble.ContextMenuStrip = _saveMenu;

        _bubble.Controls.Add(_preview);
        _bubble.Controls.Add(_progressBar);
        _bubble.Controls.Add(_statusLabel);
        _bubble.Controls.Add(_timeLabel);
        Controls.Add(_bubble);
        SizeChanged += (_, _) => LayoutBubble();
        LayoutBubble();
    }

    public string TransferId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public bool IsOwnMessage => _isOwnMessage;

    public bool IsInlineImage => _isInlineImage;

    public AttachmentTransferStage Stage { get; private set; } = AttachmentTransferStage.Preparing;

    public Rectangle BubbleBounds => _bubble.Bounds;

    public bool HasPreview => _preview.Image is not null;

    public bool HasContentBytes => _imageBytes is { Length: > 0 };

    public void UpdateProgress(
        long bytesTransferred,
        long totalBytes,
        AttachmentTransferStage stage)
    {
        Stage = stage;
        int progress = totalBytes <= 0
            ? stage == AttachmentTransferStage.Completed ? 1000 : 0
            : (int)Math.Clamp(bytesTransferred * 1000L / totalBytes, 0, 1000);
        _progressBar.Value = progress;
        _progressBar.Visible = stage is not AttachmentTransferStage.Completed
            and not AttachmentTransferStage.Failed
            and not AttachmentTransferStage.Cancelled
            and not AttachmentTransferStage.Rejected;
        _statusLabel.Visible = true;
        _statusLabel.ForeColor = Color.FromArgb(98, 109, 124);
        _statusLabel.Text = stage switch
        {
            AttachmentTransferStage.Preparing => _isInlineImage ? "正在准备图片…" : "正在准备表情…",
            AttachmentTransferStage.WaitingForPeer => "正在接收…",
            AttachmentTransferStage.Transferring => $"传输中 {progress / 10.0:0.0}%",
            AttachmentTransferStage.Verifying => "正在完成…",
            _ => _statusLabel.Text
        };
        LayoutBubble();
    }

    public void ShowLocalPreview(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        try
        {
            SetPreviewBytes(File.ReadAllBytes(localPath));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            SetPreviewFailure("无法读取预览，可右键另存为。", hasBytes: false);
        }
    }

    public void ShowVerifiedPreview(ReadOnlyMemory<byte> contentBytes)
    {
        if (contentBytes.IsEmpty)
        {
            return;
        }

        SetPreviewBytes(contentBytes.ToArray());
    }

    public void Complete(
        bool succeeded,
        AttachmentTransferStage stage,
        string message,
        string? localPath = null,
        ReadOnlyMemory<byte> contentBytes = default)
    {
        Stage = stage;
        if (succeeded)
        {
            if (!contentBytes.IsEmpty)
            {
                ShowVerifiedPreview(contentBytes);
            }
            else if (!HasContentBytes && !string.IsNullOrWhiteSpace(localPath))
            {
                ShowLocalPreview(localPath);
            }
        }

        _progressBar.Value = succeeded ? 1000 : _progressBar.Value;
        _progressBar.Visible = false;
        if (succeeded)
        {
            // A successful inline media transfer is represented by the image
            // itself. Do not expose protocol/hash acknowledgements in chat.
            _statusLabel.Visible = _previewFailure;
            if (_previewFailure)
            {
                _statusLabel.Text = "预览不可用，可右键另存为。";
                _statusLabel.ForeColor = Color.FromArgb(163, 58, 58);
            }
        }
        else
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = string.IsNullOrWhiteSpace(message) ? "传输失败。" : message;
            _statusLabel.ForeColor = Color.FromArgb(163, 58, 58);
        }

        LayoutBubble();
        Parent?.PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _saveMenu.Dispose();
            _preview.Image = null;
            _image?.Dispose();
            _image = null;
            _imageStream?.Dispose();
            _imageStream = null;
            ClearBytes();
        }

        base.Dispose(disposing);
    }

    private void SetPreviewBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            ClearBytes();
            SetPreviewFailure("预览不可用。", hasBytes: false);
            return;
        }

        Image? image = null;
        MemoryStream? stream = null;
        try
        {
            stream = new MemoryStream(bytes, writable: false);
            image = Image.FromStream(
                stream,
                useEmbeddedColorManagement: false,
                validateImageData: true);
            ReplaceImage(image, stream, bytes);
            image = null;
            stream = null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or OutOfMemoryException
                                           or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            image?.Dispose();
            stream?.Dispose();
            ReplaceImage(null, null, bytes);
            SetPreviewFailure("预览不可用，可右键另存为。", hasBytes: true);
        }
    }

    private void ReplaceImage(Image? image, MemoryStream? stream, byte[] bytes)
    {
        _preview.Image = null;
        _image?.Dispose();
        _imageStream?.Dispose();
        ClearBytes();

        _image = image;
        _imageStream = stream;
        _imageBytes = bytes;
        _preview.Image = image;
        _preview.Visible = image is not null;
        _previewFailure = image is null;
        LayoutBubble();
    }

    private void SetPreviewFailure(string message, bool hasBytes)
    {
        _previewFailure = true;
        _preview.Visible = false;
        _statusLabel.Text = message;
        _statusLabel.ForeColor = Color.FromArgb(163, 58, 58);
        _statusLabel.Visible = hasBytes || Stage != AttachmentTransferStage.Completed;
        LayoutBubble();
    }

    private void ClearBytes()
    {
        if (_imageBytes is not null)
        {
            Array.Clear(_imageBytes, 0, _imageBytes.Length);
            _imageBytes = null;
        }
    }

    private void SaveAs()
    {
        byte[]? bytes = _imageBytes;
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = _isInlineImage ? "图片另存为" : "表情另存为",
            FileName = Path.GetFileName(FileName),
            Filter = GetSaveFilter(),
            AddExtension = false,
            CheckPathExists = true,
            OverwritePrompt = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            MessageBox.Show(
                FindForm(),
                "无法保存当前图片或表情。",
                "Larkzee Chat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private string GetSaveFilter()
    {
        if (!_isInlineImage)
        {
            return "PNG 或 GIF|*.png;*.gif|所有文件|*.*";
        }

        return ContentType switch
        {
            "image/png" => "PNG 图片|*.png|所有文件|*.*",
            "image/jpeg" => "JPEG 图片|*.jpg;*.jpeg|所有文件|*.*",
            "image/gif" => "GIF 图片|*.gif|所有文件|*.*",
            "image/bmp" => "BMP 图片|*.bmp|所有文件|*.*",
            "image/webp" => "WebP 图片|*.webp|所有文件|*.*",
            _ => "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*"
        };
    }

    private void LayoutBubble()
    {
        if (ClientSize.Width <= 0)
        {
            return;
        }

        int maximumBubbleWidth = _isInlineImage
            ? ImageMaximumBubbleWidth
            : StickerMaximumBubbleWidth;
        int bubbleWidth = Math.Min(
            maximumBubbleWidth,
            Math.Max(MinimumBubbleWidth, (int)Math.Floor(ClientSize.Width * 0.66)));
        bubbleWidth = Math.Min(bubbleWidth, ClientSize.Width);
        int contentWidth = Math.Max(1, bubbleWidth - (BubblePadding * 2));
        int top = 8;
        int previewMaximumWidth = _isInlineImage
            ? ImagePreviewMaximumWidth
            : StickerPreviewMaximumWidth;
        int previewMaximumHeight = _isInlineImage
            ? ImagePreviewMaximumHeight
            : StickerPreviewMaximumHeight;
        int previewWidth = Math.Min(previewMaximumWidth, contentWidth);
        if (_preview.Image is not null)
        {
            double imageRatio = _preview.Image.Width / (double)Math.Max(1, _preview.Image.Height);
            int previewHeight = Math.Clamp(
                (int)Math.Round(previewWidth / Math.Max(0.2, imageRatio)),
                48,
                previewMaximumHeight);
            _preview.Bounds = new Rectangle(BubblePadding, top, previewWidth, previewHeight);
            top = _preview.Bottom + 6;
        }
        else
        {
            _preview.Bounds = new Rectangle(BubblePadding, top, previewWidth, 1);
        }

        if (_progressBar.Visible)
        {
            _progressBar.Bounds = new Rectangle(BubblePadding, top, contentWidth, 5);
            top = _progressBar.Bottom + 6;
        }
        else
        {
            _progressBar.Bounds = new Rectangle(BubblePadding, top, contentWidth, 5);
        }

        if (_statusLabel.Visible)
        {
            _statusLabel.Bounds = new Rectangle(BubblePadding, top, Math.Max(1, contentWidth - 58), 20);
            _timeLabel.Bounds = new Rectangle(
                bubbleWidth - BubblePadding - 52,
                top,
                52,
                20);
            top = Math.Max(_statusLabel.Bottom, _timeLabel.Bottom);
        }
        else
        {
            _statusLabel.Bounds = new Rectangle(BubblePadding, top, contentWidth, 1);
            _timeLabel.Bounds = new Rectangle(
                BubblePadding,
                top,
                contentWidth,
                20);
            top = _timeLabel.Bottom;
        }

        int bubbleHeight = top + 8;
        int bubbleLeft = _isOwnMessage ? Math.Max(0, ClientSize.Width - bubbleWidth) : 0;
        _bubble.Bounds = new Rectangle(bubbleLeft, 0, bubbleWidth, bubbleHeight);
        Height = bubbleHeight;
    }
}
