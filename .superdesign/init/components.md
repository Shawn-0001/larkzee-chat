# Shared UI components

## Repository UI stack

- Framework: .NET 8 Windows Forms (`net8.0-windows`, `UseWindowsForms=true`).
- Component library: native WinForms controls plus one project-owned reusable `UserControl`.
- Styling: imperative C# properties; there is no CSS, XAML, Tailwind, or third-party UI package.

## ChatMessageControl

- File: `Controls/ChatMessageControl.cs`
- Component: `ChatMessageControl`
- Description: Reusable left/right chat bubble that measures wrapped text, renders a timestamp, and caps the bubble at 74% of the available row width.
- Constructor props: `message` (string), `timestamp` (string), `isOwnMessage` (bool).
- Public state: `IsOwnMessage` (read-only bool).

### Full source

```csharp
namespace LarkzeeChat.Controls;

/// <summary>
/// A lightweight, native WinForms chat bubble.  It intentionally contains no
/// speaker label; alignment is the only indication of message ownership.
/// </summary>
public sealed class ChatMessageControl : UserControl
{
    private const int HorizontalMargin = 12;
    private const int BubblePadding = 14;
    private const int TimeGap = 5;

    private readonly Panel _bubble;
    private readonly Label _messageLabel;
    private readonly Label _timeLabel;
    private readonly string _message;
    private readonly string _timestamp;

    public ChatMessageControl(string message, string timestamp, bool isOwnMessage)
    {
        _message = message;
        _timestamp = timestamp;
        IsOwnMessage = isOwnMessage;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        Margin = new Padding(0);
        Padding = new Padding(0, 5, 0, 5);
        BackColor = Color.Transparent;
        TabStop = false;

        _bubble = new Panel
        {
            BackColor = IsOwnMessage
                ? Color.FromArgb(225, 240, 255)
                : Color.FromArgb(240, 240, 240),
            Padding = new Padding(BubblePadding, 9, BubblePadding, 8),
            TabStop = false
        };

        _messageLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(30, 30, 30),
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Text = _message,
            UseCompatibleTextRendering = false,
            TabStop = false
        };

        _timeLabel = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(125, 125, 125),
            Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point),
            Text = _timestamp,
            TabStop = false
        };

        _bubble.Controls.Add(_messageLabel);
        _bubble.Controls.Add(_timeLabel);
        Controls.Add(_bubble);

        SizeChanged += (_, _) => LayoutBubble();
        _bubble.Resize += (_, _) => LayoutBubble();
        LayoutBubble();
    }

    public bool IsOwnMessage { get; }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        LayoutBubble();
    }

    private void LayoutBubble()
    {
        if (ClientSize.Width <= 0 || _messageLabel is null)
        {
            return;
        }

        int availableWidth = Math.Max(120, ClientSize.Width);
        int maxBubbleWidth = Math.Max(170, (int)(availableWidth * 0.74));
        int maxTextWidth = Math.Max(90, maxBubbleWidth - (BubblePadding * 2));

        Size measured = TextRenderer.MeasureText(
            _message,
            _messageLabel.Font,
            new Size(maxTextWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        int textWidth = Math.Clamp(measured.Width, 1, maxTextWidth);
        int textHeight = Math.Max(_messageLabel.Font.Height, measured.Height);
        _messageLabel.Bounds = new Rectangle(BubblePadding, 9, textWidth, textHeight);

        int bubbleWidth = textWidth + (BubblePadding * 2);
        int timeTop = _messageLabel.Bottom + TimeGap;
        _timeLabel.Location = new Point(BubblePadding, timeTop);
        int bubbleHeight = _timeLabel.Bottom + 8;
        _bubble.Bounds = new Rectangle(
            IsOwnMessage
                ? Math.Max(HorizontalMargin, ClientSize.Width - bubbleWidth - HorizontalMargin)
                : HorizontalMargin,
            5,
            bubbleWidth,
            bubbleHeight);

        Height = bubbleHeight + 10;
    }
}
```

## Native primitives assembled inline

`MainForm` and `SettingsForm` compose native `Button`, `TextBox`, `Label`, `Panel`, `FlowLayoutPanel`, `TableLayoutPanel`, `GroupBox`, and `CheckBox` controls directly. They are page/layout-specific today rather than separate shared component files.
