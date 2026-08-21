namespace LarkzeeChat.Controls;

/// <summary>
/// A compact native WinForms chat bubble. Ownership is communicated by
/// alignment only; no speaker labels or technical metadata are rendered.
/// </summary>
public sealed class ChatMessageControl : UserControl
{
    private const int BubblePadding = 14;
    private const int BubbleTopPadding = 10;
    private const int BubbleBottomPadding = 9;
    private const int TimeGap = 5;
    private const int MeasurementWidthAllowance = 2;
    private const int MeasurementHeightAllowance = 2;
    private const int MaximumBubbleWidth = 460;

    private readonly RoundedBorderPanel _bubble;
    private readonly Label _messageLabel;
    private readonly Label _timeLabel;
    private readonly string _message;
    private readonly string _timestamp;
    private readonly Dictionary<int, BubbleMetrics> _layoutCache = [];
    private int _lastLayoutWidth = -1;

    private readonly record struct BubbleMetrics(
        int TextWidth,
        int TextHeight,
        int TimeHeight);

    public ChatMessageControl(string message, string timestamp, bool isOwnMessage)
    {
        _message = message ?? string.Empty;
        _timestamp = timestamp ?? string.Empty;
        IsOwnMessage = isOwnMessage;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        Margin = new Padding(0, 0, 0, 8);
        Padding = Padding.Empty;
        // Keep each row opaque. Transparent UserControls can ask their parent
        // to repaint during a resize, which is the source of the stacked
        // message silhouettes seen while dragging the window.
        BackColor = Color.FromArgb(247, 248, 250); // #F7F8FA
        TabStop = false;

        _bubble = new RoundedBorderPanel
        {
            BackColor = IsOwnMessage
                ? Color.FromArgb(220, 238, 255) // #DCEEFF
                : Color.White,
            BorderColor = IsOwnMessage
                ? Color.FromArgb(199, 225, 250) // #C7E1FA
                : Color.FromArgb(225, 229, 234), // #E1E5EA
            CornerRadius = 11,
            TabStop = false
        };

        _messageLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(24, 34, 48), // #182230
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Text = _message,
            UseCompatibleTextRendering = false,
            UseMnemonic = false,
            TabStop = false
        };

        _timeLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(122, 132, 146), // #7A8492
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            Text = _timestamp,
            UseMnemonic = false,
            TabStop = false
        };

        _bubble.Controls.Add(_messageLabel);
        _bubble.Controls.Add(_timeLabel);
        Controls.Add(_bubble);

        SizeChanged += (_, _) => LayoutBubble();
        LayoutBubble();
    }

    public bool IsOwnMessage { get; }

    // These read-only values keep structural UI checks independent of private
    // control names while leaving the rendered message immutable.
    public string MessageText => _message;

    public string DisplayTimestamp => _timestamp;

    public Color BubbleBackColor => _bubble.BackColor;

    public Color BubbleBorderColor => _bubble.BorderColor;

    public int BubbleCornerRadius => _bubble.CornerRadius;

    public Rectangle BubbleBounds => _bubble.Bounds;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _layoutCache.Clear();
        _lastLayoutWidth = -1;
        LayoutBubble();
    }

    private void LayoutBubble()
    {
        if (ClientSize.Width <= 0)
        {
            return;
        }

        // The parent gives this control the full feed width. The actual bubble
        // is then measured inside the approved 66% / 460px maximum.
        int availableWidth = Math.Max(1, ClientSize.Width);
        if (_lastLayoutWidth == availableWidth)
        {
            return;
        }

        _lastLayoutWidth = availableWidth;
        int maxBubbleWidth = Math.Min(
            MaximumBubbleWidth,
            Math.Max(1, (int)Math.Floor(availableWidth * 0.66)));
        int maxTextWidth = Math.Max(1, maxBubbleWidth - (BubblePadding * 2));
        if (!_layoutCache.TryGetValue(maxTextWidth, out BubbleMetrics metrics))
        {
            string measurableText = _message.Length == 0 ? " " : _message;
            const TextFormatFlags measureFlags = TextFormatFlags.WordBreak
                | TextFormatFlags.TextBoxControl
                | TextFormatFlags.NoPrefix;

            Size naturalSize = TextRenderer.MeasureText(
                measurableText,
                _messageLabel.Font,
                new Size(int.MaxValue, int.MaxValue),
                measureFlags);
            bool wraps = naturalSize.Width > maxTextWidth;
            int measuredTextWidth = wraps
                ? maxTextWidth
                : Math.Min(maxTextWidth, naturalSize.Width + MeasurementWidthAllowance);
            Size measuredSize =
                TextRenderer.MeasureText(
                    measurableText,
                    _messageLabel.Font,
                    new Size(Math.Max(1, measuredTextWidth), int.MaxValue),
                    measureFlags);
            int timestampWidth = TextRenderer.MeasureText(
                _timestamp.Length == 0 ? " " : _timestamp,
                _timeLabel.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix).Width;
            int textWidth = Math.Clamp(
                Math.Max(measuredTextWidth, timestampWidth + MeasurementWidthAllowance),
                1,
                maxTextWidth);
            if (textWidth != measuredTextWidth)
            {
                measuredSize = TextRenderer.MeasureText(
                    measurableText,
                    _messageLabel.Font,
                    new Size(textWidth, int.MaxValue),
                    measureFlags);
            }

            int textHeight = Math.Max(
                _messageLabel.Font.Height,
                measuredSize.Height) + MeasurementHeightAllowance;
            Size measuredTimestamp = TextRenderer.MeasureText(
                _timestamp.Length == 0 ? " " : _timestamp,
                _timeLabel.Font,
                new Size(textWidth, int.MaxValue),
                TextFormatFlags.NoPrefix);
            int timeHeight = Math.Max(
                _timeLabel.Font.Height,
                measuredTimestamp.Height) + MeasurementHeightAllowance;
            metrics = new BubbleMetrics(textWidth, textHeight, timeHeight);
            _layoutCache[maxTextWidth] = metrics;
        }

        _messageLabel.Bounds = new Rectangle(
            BubblePadding,
            BubbleTopPadding,
            metrics.TextWidth,
            metrics.TextHeight);

        int timeTop = _messageLabel.Bottom + TimeGap;
        _timeLabel.Bounds = new Rectangle(
            BubblePadding,
            timeTop,
            metrics.TextWidth,
            metrics.TimeHeight);

        int bubbleWidth = metrics.TextWidth + (BubblePadding * 2);
        int bubbleHeight = _timeLabel.Bottom + BubbleBottomPadding;
        int bubbleLeft = IsOwnMessage
            ? Math.Max(0, ClientSize.Width - bubbleWidth)
            : 0;
        var bubbleBounds = new Rectangle(
            bubbleLeft,
            0,
            bubbleWidth,
            bubbleHeight);

        if (_bubble.Bounds != bubbleBounds)
        {
            _bubble.Bounds = bubbleBounds;
        }

        if (Height != bubbleHeight)
        {
            Height = bubbleHeight;
        }
    }
}
