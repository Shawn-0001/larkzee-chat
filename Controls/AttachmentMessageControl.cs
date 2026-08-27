using LarkzeeChat.Networking;

namespace LarkzeeChat.Controls;

public sealed class AttachmentMessageControl : UserControl
{
    private const int MaximumBubbleWidth = 460;
    private const int MinimumBubbleWidth = 300;
    private const int BubblePadding = 14;
    private const int ImagePreviewHeight = 132;
    private const long MaximumPreviewSourceBytes = 25L * 1024 * 1024;

    private readonly RoundedBorderPanel _bubble;
    private readonly Label _kindLabel;
    private readonly Label _fileNameLabel;
    private readonly Label _sizeLabel;
    private readonly PictureBox _preview;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _timeLabel;
    private readonly Button _openFolderButton;
    private readonly bool _isImage;
    private string? _localPath;

    public AttachmentMessageControl(
        string transferId,
        string fileName,
        string contentType,
        long fileSize,
        bool isOwnMessage,
        string timestamp)
    {
        TransferId = transferId;
        FileName = fileName;
        ContentType = contentType;
        FileSize = fileSize;
        IsOwnMessage = isOwnMessage;
        _isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

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
            TabStop = false
        };
        _kindLabel = new Label
        {
            AutoSize = false,
            Text = _isImage ? "图片" : "文件",
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(24, 119, 210),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        _fileNameLabel = new Label
        {
            AutoSize = false,
            Text = fileName,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(24, 34, 48),
            AutoEllipsis = true,
            UseMnemonic = false,
            AccessibleName = "附件文件名"
        };
        _sizeLabel = new Label
        {
            AutoSize = false,
            Text = FormatFileSize(fileSize),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(122, 132, 146),
            UseMnemonic = false
        };
        _preview = new PictureBox
        {
            BackColor = Color.FromArgb(238, 242, 247),
            BorderStyle = BorderStyle.None,
            SizeMode = PictureBoxSizeMode.Zoom,
            Visible = _isImage,
            AccessibleName = "图片预览"
        };
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            TabStop = false,
            AccessibleName = "附件传输进度"
        };
        _statusLabel = new Label
        {
            AutoSize = false,
            Text = "正在准备…",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(98, 109, 124),
            AutoEllipsis = true,
            UseMnemonic = false,
            AccessibleName = "附件传输状态"
        };
        _timeLabel = new Label
        {
            AutoSize = false,
            Text = timestamp,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(122, 132, 146),
            TextAlign = ContentAlignment.MiddleRight,
            UseMnemonic = false
        };
        _openFolderButton = new Button
        {
            Text = "打开文件夹",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(24, 119, 210),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            Size = new Size(88, 27),
            Visible = false,
            TabStop = true,
            AccessibleName = "打开附件所在文件夹"
        };
        _openFolderButton.FlatAppearance.BorderColor = Color.FromArgb(199, 225, 250);
        _openFolderButton.FlatAppearance.BorderSize = 1;
        _openFolderButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_localPath))
            {
                OpenFolderRequested?.Invoke(this, new OpenAttachmentFolderEventArgs(_localPath));
            }
        };

        _bubble.Controls.Add(_kindLabel);
        _bubble.Controls.Add(_fileNameLabel);
        _bubble.Controls.Add(_sizeLabel);
        _bubble.Controls.Add(_preview);
        _bubble.Controls.Add(_progressBar);
        _bubble.Controls.Add(_statusLabel);
        _bubble.Controls.Add(_timeLabel);
        _bubble.Controls.Add(_openFolderButton);
        Controls.Add(_bubble);
        SizeChanged += (_, _) => LayoutBubble();
        LayoutBubble();
    }

    public event EventHandler<OpenAttachmentFolderEventArgs>? OpenFolderRequested;

    public string TransferId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public long FileSize { get; }

    public bool IsOwnMessage { get; }

    public Rectangle BubbleBounds => _bubble.Bounds;

    public AttachmentTransferStage Stage { get; private set; } = AttachmentTransferStage.Preparing;

    public void UpdateProgress(
        long bytesTransferred,
        long totalBytes,
        AttachmentTransferStage stage)
    {
        Stage = stage;
        int progress = totalBytes <= 0
            ? (stage == AttachmentTransferStage.Completed ? 1000 : 0)
            : (int)Math.Clamp(bytesTransferred * 1000L / totalBytes, 0, 1000);
        _progressBar.Value = progress;
        _statusLabel.ForeColor = Color.FromArgb(98, 109, 124);
        _statusLabel.Text = stage switch
        {
            AttachmentTransferStage.Preparing => "正在计算文件校验值…",
            AttachmentTransferStage.WaitingForPeer => "等待对方选择保存位置…",
            AttachmentTransferStage.Transferring => $"传输中 {progress / 10.0:0.0}%",
            AttachmentTransferStage.Verifying => "传输完成，正在校验…",
            _ => _statusLabel.Text
        };
    }

    public void ShowLocalPreview(string localPath)
    {
        if (_isImage && !string.IsNullOrWhiteSpace(localPath))
        {
            TryLoadPreview(localPath);
        }
    }

    public void Complete(
        bool succeeded,
        AttachmentTransferStage stage,
        string message,
        string? localPath)
    {
        Stage = stage;
        _localPath = succeeded && !string.IsNullOrWhiteSpace(localPath) ? localPath : null;
        _progressBar.Value = succeeded ? 1000 : _progressBar.Value;
        _progressBar.Visible = false;
        _statusLabel.Text = message;
        _statusLabel.ForeColor = succeeded
            ? Color.FromArgb(23, 134, 75)
            : Color.FromArgb(163, 58, 58);
        _openFolderButton.Visible = _localPath is not null;
        if (succeeded && _isImage && _localPath is not null)
        {
            TryLoadPreview(_localPath);
        }

        LayoutBubble();
        Parent?.PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Image? image = _preview.Image;
            _preview.Image = null;
            image?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LayoutBubble()
    {
        if (ClientSize.Width <= 0)
        {
            return;
        }

        int bubbleWidth = Math.Min(
            MaximumBubbleWidth,
            Math.Max(MinimumBubbleWidth, (int)Math.Floor(ClientSize.Width * 0.66)));
        bubbleWidth = Math.Min(bubbleWidth, ClientSize.Width);
        int contentWidth = Math.Max(1, bubbleWidth - (BubblePadding * 2));
        int top = 10;

        _kindLabel.Bounds = new Rectangle(BubblePadding, top, 44, 20);
        _timeLabel.Bounds = new Rectangle(bubbleWidth - BubblePadding - 52, top, 52, 20);
        _fileNameLabel.Bounds = new Rectangle(BubblePadding, _kindLabel.Bottom + 4, contentWidth, 23);
        _sizeLabel.Bounds = new Rectangle(BubblePadding, _fileNameLabel.Bottom + 1, contentWidth, 19);
        top = _sizeLabel.Bottom + 7;
        if (_isImage)
        {
            _preview.Bounds = new Rectangle(BubblePadding, top, contentWidth, ImagePreviewHeight);
            top = _preview.Bottom + 8;
        }

        _progressBar.Bounds = new Rectangle(BubblePadding, top, contentWidth, 6);
        if (_progressBar.Visible)
        {
            top = _progressBar.Bottom + 7;
        }

        int statusWidth = _openFolderButton.Visible
            ? Math.Max(1, contentWidth - _openFolderButton.Width - 8)
            : contentWidth;
        _statusLabel.Bounds = new Rectangle(BubblePadding, top, statusWidth, 27);
        _openFolderButton.Location = new Point(
            bubbleWidth - BubblePadding - _openFolderButton.Width,
            top);
        int bubbleHeight = Math.Max(_statusLabel.Bottom, _openFolderButton.Bottom) + 10;
        int bubbleLeft = IsOwnMessage ? Math.Max(0, ClientSize.Width - bubbleWidth) : 0;
        _bubble.Bounds = new Rectangle(bubbleLeft, 0, bubbleWidth, bubbleHeight);
        Height = bubbleHeight;
    }

    private void TryLoadPreview(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumPreviewSourceBytes)
            {
                return;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            int width = Math.Max(1, _preview.ClientSize.Width);
            int height = Math.Max(1, _preview.ClientSize.Height);
            var preview = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.Clear(_preview.BackColor);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                Rectangle destination = FitInside(source.Size, new Size(width, height));
                graphics.DrawImage(source, destination);
            }

            Image? previous = _preview.Image;
            _preview.Image = preview;
            previous?.Dispose();
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private static Rectangle FitInside(Size source, Size bounds)
    {
        double scale = Math.Min(
            bounds.Width / (double)Math.Max(1, source.Width),
            bounds.Height / (double)Math.Max(1, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle(
            (bounds.Width - width) / 2,
            (bounds.Height - height) / 2,
            width,
            height);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}

public sealed class OpenAttachmentFolderEventArgs : EventArgs
{
    public OpenAttachmentFolderEventArgs(string path)
    {
        Path = path;
    }

    public string Path { get; }
}
