using System.Runtime.InteropServices;

namespace BatomonHelper.Overlay;

internal static class NativeOverlay
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool ExcludeFromCapture(IntPtr hwnd) =>
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture) &&
        GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == WdaExcludeFromCapture;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)(enabled ? style | WsExTransparent : style & ~WsExTransparent));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    public static void ApplyWorkAreaMaximize(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        bounds.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        bounds.MaxSize.X = info.Work.Right - info.Work.Left;
        bounds.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
    }

    public static void ClampToCurrentWorkArea(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        // Native monitor coordinates are pixels; WPF dimensions are DIPs.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var workWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
        var workHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
        window.MinWidth = Math.Min(960, workWidth);
        window.MinHeight = Math.Min(640, workHeight);
        window.Width = Math.Min(window.Width, workWidth);
        window.Height = Math.Min(window.Height, workHeight);
        var width = (int)Math.Round(window.Width * dpi.DpiScaleX);
        var height = (int)Math.Round(window.Height * dpi.DpiScaleY);
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            info.Work.Left + ((info.Work.Right - info.Work.Left - width) / 2),
            info.Work.Top + ((info.Work.Bottom - info.Work.Top - height) / 2),
            width,
            height,
            SwpNoZOrder | SwpNoActivate);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKeyCode);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
