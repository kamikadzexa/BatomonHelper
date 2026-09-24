using System.Runtime.InteropServices;

namespace BatomonHelper.Overlay;

internal static class NativeOverlay
{
    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020L;

    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)(enabled ? style | WsExTransparent : style & ~WsExTransparent));
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKeyCode);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterHotKey(IntPtr handle, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
