using System.Windows.Interop;

namespace BatomonHelper.Overlay;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xB470;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new OverlayViewModel();
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            NativeOverlay.RegisterHotKey(_source!.Handle, HotkeyId, 0, 0x77); // F8
        };
        Closed += (_, _) => { if (_source is not null) NativeOverlay.UnregisterHotKey(_source.Handle, HotkeyId); };
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void DragWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
