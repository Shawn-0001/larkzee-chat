namespace LarkzeeChat.Controls;

/// <summary>
/// A double-buffered top-down feed that coalesces viewport notifications.
/// FlowLayoutPanel can lay out once before its vertical scrollbar is created
/// and once again after the scrollbar reduces the usable width. Consumers use
/// <see cref="ViewportLayoutChanged"/> to apply child widths after that pair
/// of layouts has settled.
/// </summary>
public sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    private bool _viewportNotificationPending;
    private int _lastViewportWidth = -1;
    private int _lastViewportHeight = -1;
    private bool _lastHorizontalScrollVisible;
    private bool _lastVerticalScrollVisible;

    public BufferedFlowLayoutPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    public event EventHandler? ViewportLayoutChanged;

    public bool HasHorizontalScrollBar => HorizontalScroll.Visible;

    /// <summary>
    /// Requests one deferred synchronization even when only the content (and
    /// not the viewport dimensions) changed.
    /// </summary>
    public void RequestViewportSynchronization()
    {
        ScheduleViewportNotification(force: true);
    }

    /// <summary>
    /// FlowLayoutPanel's non-default layout engine does not include its last
    /// child's bottom margin or container padding in the automatic scroll
    /// extent. Keep an explicit vertical extent so the final bubble can be
    /// scrolled completely above the viewport edge.
    /// </summary>
    public bool SynchronizeVerticalScrollExtent()
    {
        int scrollOffsetY = AutoScrollPosition.Y;
        int contentBottom = 0;
        foreach (Control control in Controls)
        {
            if (!control.Visible || control.IsDisposed)
            {
                continue;
            }

            int unscrolledBottom = control.Bottom - scrollOffsetY + control.Margin.Bottom;
            contentBottom = Math.Max(contentBottom, unscrolledBottom);
        }

        var desired = contentBottom == 0
            ? Size.Empty
            : new Size(0, contentBottom + Padding.Bottom);
        if (AutoScrollMinSize == desired)
        {
            return false;
        }

        AutoScrollMinSize = desired;
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ScheduleViewportNotification(force: true);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        ScheduleViewportNotification();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        ScheduleViewportNotification();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        ScheduleViewportNotification(force: true);
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        ScheduleViewportNotification(force: true);
    }

    private void ScheduleViewportNotification(bool force = false)
    {
        if (_viewportNotificationPending
            || IsDisposed
            || Disposing
            || !IsHandleCreated)
        {
            return;
        }

        int viewportWidth = ClientSize.Width;
        int viewportHeight = ClientSize.Height;
        bool horizontalScrollVisible = HorizontalScroll.Visible;
        bool verticalScrollVisible = VerticalScroll.Visible;
        if (!force
            && viewportWidth == _lastViewportWidth
            && viewportHeight == _lastViewportHeight
            && horizontalScrollVisible == _lastHorizontalScrollVisible
            && verticalScrollVisible == _lastVerticalScrollVisible)
        {
            return;
        }

        _lastViewportWidth = viewportWidth;
        _lastViewportHeight = viewportHeight;
        _lastHorizontalScrollVisible = horizontalScrollVisible;
        _lastVerticalScrollVisible = verticalScrollVisible;
        _viewportNotificationPending = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _viewportNotificationPending = false;
                if (!IsDisposed && !Disposing)
                {
                    ViewportLayoutChanged?.Invoke(this, EventArgs.Empty);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            _viewportNotificationPending = false;
        }
    }
}
