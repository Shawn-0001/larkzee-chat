using System.Runtime.InteropServices;

namespace LarkzeeChat.Services;

/// <summary>
/// Uses the native Windows taskbar attention indicator for unread messages.
/// </summary>
internal static class TaskbarNotificationService
{
    private const uint FlashStop = 0x00000000;
    private const uint FlashTray = 0x00000002;
    private const uint FlashTimerNoForeground = 0x0000000C;

    internal static void FlashUntilForeground(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsDisposed
            || form.Disposing
            || !form.IsHandleCreated
            || !form.ShowInTaskbar
            || Form.ActiveForm is not null)
        {
            return;
        }

        FlashWindowInfo info = CreateInfo(
            form.Handle,
            FlashTray | FlashTimerNoForeground,
            uint.MaxValue);
        _ = FlashWindowEx(ref info);
    }

    internal static void Stop(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsDisposed || form.Disposing || !form.IsHandleCreated)
        {
            return;
        }

        FlashWindowInfo info = CreateInfo(form.Handle, FlashStop, 0);
        _ = FlashWindowEx(ref info);
    }

    private static FlashWindowInfo CreateInfo(IntPtr windowHandle, uint flags, uint count)
    {
        return new FlashWindowInfo
        {
            Size = checked((uint)Marshal.SizeOf<FlashWindowInfo>()),
            WindowHandle = windowHandle,
            Flags = flags,
            Count = count,
            TimeoutMilliseconds = 0
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo flashInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        internal uint Size;
        internal IntPtr WindowHandle;
        internal uint Flags;
        internal uint Count;
        internal uint TimeoutMilliseconds;
    }
}
