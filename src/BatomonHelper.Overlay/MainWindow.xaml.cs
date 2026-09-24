using System.Windows.Interop;

namespace BatomonHelper.Overlay;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xB470;
    private const int CaptureHotkeyId = 0xB471;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private bool _isExpanded;
    private Size _compactSize;
    private Rect _windowedBounds;
    private WindowState _windowedState;
    private WindowStyle _windowedStyle;
    private ResizeMode _windowedResizeMode;

    public static readonly DependencyProperty IsFullscreenProperty = DependencyProperty.Register(
        nameof(IsFullscreen), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        private set => SetValue(IsFullscreenProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new OverlayViewModel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && IsFullscreen)
            {
                ToggleFullscreen(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            NativeOverlay.RegisterHotKey(_source!.Handle, HotkeyId, 0, 0x77); // F8
            NativeOverlay.RegisterHotKey(_source.Handle, CaptureHotkeyId, 0, 0x78); // F9
        };
        Closed += (_, _) => { if (_source is not null) { NativeOverlay.UnregisterHotKey(_source.Handle, HotkeyId); NativeOverlay.UnregisterHotKey(_source.Handle, CaptureHotkeyId); } };
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            handled = true;
        }
        if (message == WmHotkey && wParam.ToInt32() == CaptureHotkeyId)
        {
            if (DataContext is OverlayViewModel viewModel) _ = viewModel.CaptureShopAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void DragWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    private void ToggleExpanded(object sender, RoutedEventArgs e)
    {
        if (!_isExpanded)
        {
            _compactSize = new Size(Width, Height);
            WindowState = WindowState.Normal;
            Width = Math.Min(1600, SystemParameters.WorkArea.Width);
            Height = Math.Min(920, SystemParameters.WorkArea.Height);
            ExpandButton.Content = "Compact";
            _isExpanded = true;
            return;
        }

        Width = Math.Max(MinWidth, _compactSize.Width);
        Height = Math.Max(MinHeight, _compactSize.Height);
        ExpandButton.Content = "Expand";
        _isExpanded = false;
    }

    private void ToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (!IsFullscreen)
        {
            _windowedBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            _windowedState = WindowState;
            _windowedStyle = WindowStyle;
            _windowedResizeMode = ResizeMode;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            NativeOverlay.FillCurrentMonitor(this);
            FullscreenButton.Content = "Exit Fullscreen";
            IsFullscreen = true;
            return;
        }

        WindowState = WindowState.Normal;
        WindowStyle = _windowedStyle;
        ResizeMode = _windowedResizeMode;
        Left = _windowedBounds.Left;
        Top = _windowedBounds.Top;
        Width = _windowedBounds.Width;
        Height = _windowedBounds.Height;
        WindowState = _windowedState;
        FullscreenButton.Content = "Fullscreen";
        IsFullscreen = false;
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
