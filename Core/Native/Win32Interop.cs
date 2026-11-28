using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ChGhost.Core.Native;

public static class Win32Interop
{
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const uint WdaNone = 0x00000000;
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExToolWindow = 0x00000080;
    public const int WmHotkey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModNoRepeat = 0x4000;
    public const uint MOD_NOREPEAT = ModNoRepeat;
    public const int VkLButton = 0x01;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    public const int WmDpiChanged = 0x02E0;

    public static bool TrySetWindowDisplayAffinity(IntPtr window, uint affinity, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.SetWindowDisplayAffinity(window, affinity);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static bool TrySetExcludeFromCapture(IntPtr hwnd, bool exclude)
    {
        Marshal.SetLastPInvokeError(0);
        return NativeMethods.SetWindowDisplayAffinity(hwnd, exclude ? WdaExcludeFromCapture : WdaNone);
    }

    public static void ApplyNoActivateToolStyle(IntPtr hwnd)
    {
        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        TrySetWindowLong(hwnd, GwlExStyle, exStyle | WsExNoActivate | WsExToolWindow, out _);
    }

    public static bool TrySetWindowLong(IntPtr window, int index, int value, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = SetWindowLongPtr(window, index, new IntPtr(value));
        errorCode = previous == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return errorCode == 0;
    }

    public static int GetWindowLong(IntPtr window, int index) =>
        GetWindowLongPtr(window, index).ToInt32();

    public static bool TryRegisterHotKey(IntPtr window, int id, uint modifiers, uint key, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.RegisterHotKey(window, id, modifiers, key);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static bool UnregisterHotKey(IntPtr window, int id) =>
        NativeMethods.UnregisterHotKey(window, id);

    public static bool TryUnregisterHotKey(IntPtr window, int id, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.UnregisterHotKey(window, id);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static Point GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out Point point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
        return point;
    }

    public static bool IsLeftMouseButtonDown() =>
        (NativeMethods.GetAsyncKeyState(VkLButton) & 0x8000) != 0;

    public static Rectangle GetWindowRectangle(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out NativeRect rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        return Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

    public static Rectangle GetVirtualScreenBounds()
    {
        int x = NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return width > 0 && height > 0 ? new Rectangle(x, y, width, height) : Rectangle.Empty;
    }

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8
            ? NativeMethods.GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(NativeMethods.GetWindowLong32(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? NativeMethods.SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(NativeMethods.SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    public static bool TryGetMonitorBoundsForRect(
        Rectangle rect, out Rectangle monitorBounds, out bool fullyContained)
    {
        var r = new RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        IntPtr hMon = NativeMethods.MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(hMon, ref mi))
            {
                monitorBounds = Rectangle.FromLTRB(
                    mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
                fullyContained = rect.Left >= monitorBounds.Left
                    && rect.Top >= monitorBounds.Top
                    && rect.Right <= monitorBounds.Right
                    && rect.Bottom <= monitorBounds.Bottom;
                return true;
            }
        }

        monitorBounds = GetVirtualScreenBounds();
        fullyContained = false;
        return false;
    }

    public static bool SetOverlayBoundsNoActivate(IntPtr hwnd, Rectangle physicalBounds)
        => NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero,
            physicalBounds.Left, physicalBounds.Top, physicalBounds.Width, physicalBounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        internal static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        internal static extern int SetWindowLong32(IntPtr window, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);
    }
}
