namespace LarkzeeChat.Controls;

/// <summary>
/// Small built-in WinForms surface used for the rounded message bubbles and
/// composer frame. It deliberately has no theme or dependency beyond GDI+.
/// </summary>
public sealed class RoundedBorderPanel : Panel
{
    private Color _borderColor = Color.FromArgb(220, 225, 232);
    private int _cornerRadius = 10;

    public RoundedBorderPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.White;
    }

    public Color BorderColor
    {
        get => _borderColor;
        set
        {
            if (_borderColor == value)
            {
                return;
            }

            _borderColor = value;
            Invalidate();
        }
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            int normalized = Math.Max(2, value);
            if (_cornerRadius == normalized)
            {
                return;
            }

            _cornerRadius = normalized;
            Invalidate();
        }
    }

    /// <summary>
    /// The control intentionally never assigns a native window region. A
    /// region is cached by Win32 and can leave stale rounded edges behind
    /// while a form is being resized. The rounded shape is visual only and is
    /// painted on each frame instead.
    /// </summary>
    public bool UsesVisualOnlyRounding => Region is null;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Erase the entire rectangular surface first. This is important for
        // resize/scroll frames: pixels outside the rounded path must be
        // repainted before the next rounded fill is drawn.
        Color eraseColor = Parent?.BackColor ?? SystemColors.Control;
        using var eraseBrush = new SolidBrush(eraseColor);
        e.Graphics.FillRectangle(eraseBrush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = CreatePath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1));
        using var fill = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColor, 1F);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Invalidate();
    }

    private System.Drawing.Drawing2D.GraphicsPath CreatePath(Rectangle bounds)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), CornerRadius * 2);
        if (diameter <= 2)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
