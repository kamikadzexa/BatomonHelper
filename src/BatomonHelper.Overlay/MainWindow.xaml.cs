using System.Windows.Interop;
using System.Windows.Controls;
using System.Diagnostics;

namespace BatomonHelper.Overlay;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xB470;
    private const int CaptureHotkeyId = 0xB471;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private WindowState _lastVisibleState = WindowState.Normal;

    // Fullscreen now uses the same work-area bounds as standard maximize.
    public static readonly DependencyProperty IsFullscreenProperty = DependencyProperty.Register(
        nameof(IsFullscreen), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        private set => SetValue(IsFullscreenProperty, value);
    }

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        DataContext = new OverlayViewModel();
        var setup = ((OverlayViewModel)DataContext).Setup;
        var neededSetup = setup.NeedsSetup;
        if (neededSetup) BoardTabs.SelectedItem = GameSetupTab;
        setup.PropertyChanged += (_, _) =>
        {
            var needsSetup = setup.NeedsSetup;
            if (needsSetup && !neededSetup) BoardTabs.SelectedItem = GameSetupTab;
            neededSetup = needsSetup;
        };
        SizeChanged += (_, _) => UpdateBoardLayout();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastVisibleState = WindowState;
            IsFullscreen = WindowState == WindowState.Maximized;
            MaximizeButton.Content = IsFullscreen ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = IsFullscreen ? "Restore" : "Maximize";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, IsFullscreen ? "Restore" : "Maximize");
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsFullscreen)
            {
                ToggleMaximize(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source!.AddHook(WndProc);
            NativeOverlay.ClampToCurrentWorkArea(this);
            var visibilityKey = NativeOverlay.RegisterHotKey(_source.Handle, HotkeyId, 0x4000, 0x77);
            var captureKey = NativeOverlay.RegisterHotKey(_source.Handle, CaptureHotkeyId, 0x4000, 0x78);
            if ((!visibilityKey || !captureKey) && DataContext is OverlayViewModel keyModel)
                keyModel.ReportHotkeyConflict($"Could not register {(!visibilityKey ? "F8 visibility " : "")}{(!captureKey ? "F9 refresh" : "")}: another application owns the shortcut.");
        };
        Closed += (_, _) =>
        {
            if (DataContext is OverlayViewModel viewModel) viewModel.StopCaptures();
            if (_source is null) return;
            NativeOverlay.UnregisterHotKey(_source.Handle, HotkeyId);
            NativeOverlay.UnregisterHotKey(_source.Handle, CaptureHotkeyId);
            _source.RemoveHook(WndProc);
        };
    }

    private void DashboardSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBoardLayout();

    private void UpdateBoardLayout()
    {
        if (BoardColumns is null || BoardAnalysis is null) return;
        var wide = ShellRoot.ActualWidth >= 1200;
        IsCompact = !wide;
        BoardColumns.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(BoardAnalysis, wide ? 1 : 0);
        Grid.SetRow(BoardAnalysis, wide ? 0 : 1);
        BoardAnalysis.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 16, 0, 0);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) NativeOverlay.ApplyWorkAreaMaximize(hwnd, lParam, ref handled);
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleVisibility();
            handled = true;
        }
        if (message == WmHotkey && wParam.ToInt32() == CaptureHotkeyId)
        {
            if (DataContext is OverlayViewModel viewModel) _ = viewModel.CaptureShopAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void HideHelper(object sender, RoutedEventArgs e) => ToggleVisibility();

    internal void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = _lastVisibleState;
        }
    }

    private void ToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void OpenReleasesPage(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(GitHubReleaseUpdater.ReleasesPage) { UseShellExecute = true });
}
