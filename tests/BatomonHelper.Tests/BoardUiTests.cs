using BatomonHelper.Core;
using BatomonHelper.Overlay;
using BatomonHelper.ScreenReader;
using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BatomonHelper.Tests;

[CollectionDefinition("WPF board", DisableParallelization = true)]
public sealed class BoardUiCollection;

[Collection("WPF board")]
public sealed class BoardUiTests
{
    [Fact]
    public async Task Board_and_correction_views_render_and_support_added_badges()
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "board-day3.png"));
        var result = (await new WindowsBoardScreenReader().RecognizeImageAsync(image, catalog)).MaterializeFrame();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = new MainWindow();
                Assert.Equal(1440, window.Width);
                Assert.Equal(900, window.Height);
                Assert.Equal(960, window.MinWidth);
                Assert.Equal(640, window.MinHeight);
                window.Width = 1300; window.Height = 1000;
                var viewModel = (OverlayViewModel)window.DataContext;
                viewModel.AutoUpdate = false;
                typeof(OverlayViewModel).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, catalog);
                typeof(OverlayViewModel).GetMethod("RenderBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [result.Snapshot]);
                Assert.Equal(6, viewModel.TeamRows.Count); Assert.Equal(4, viewModel.BenchRows.Count); Assert.Equal(5, viewModel.OfferRows.Count);
                Assert.Contains("Lv. 2", viewModel.TeamRows[5].Level); Assert.Contains("Damage 15", viewModel.TeamRows[5].Stats);
                typeof(OverlayViewModel).GetProperty(nameof(OverlayViewModel.DamageSummary))!.SetValue(viewModel, "Next opponent HP (est.): 38,600  ·  Estimated win at 18.2s");
                viewModel.DamageRows.Add(new("DPS", "120.0", "180.0", "240.0", "360.0", "480.0", "600.0", ""));
                viewModel.DamageRows.Add(new("Poison /s", "8.0", "10.0", "12.0", "16.0", "20.0", "24.0", ""));
                viewModel.DamageRows.Add(new("Electricity /s", "5.0", "8.0", "10.0", "15.0", "20.0", "25.0", ""));
                viewModel.DamageRows.Add(new("Shield /s", "100.0", "120.0", "150.0", "200.0", "250.0", "300.0", ""));
                Render((FrameworkElement)window.Content, 1300, 1000, "board-ui.png");
                var tabs = (TabControl)window.FindName("BoardTabs");
                Assert.DoesNotContain(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "DPS"));
                Render((FrameworkElement)window.Content, 1300, 1000, "board-dps-ui.png");
                var rawBattle = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-battle-outcome.json"));
                var exportedBattle = System.Text.Json.JsonSerializer.Deserialize<GameStateMessage>(rawBattle, GameStateBridge.JsonOptions)!;
                var battle = GameStateBridge.Parse(rawBattle, catalog, exportedBattle.UpdatedAt).Battle!;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                var refresh = (Task)typeof(OverlayViewModel).GetMethod("RefreshBattleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [battle])!;
                var frame = new System.Windows.Threading.DispatcherFrame();
                _ = refresh.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                refresh.GetAwaiter().GetResult();
                Assert.Equal("WIN", viewModel.BattleSummary);
                Assert.Equal(6, viewModel.OpponentRows.Count);
                Assert.Contains(viewModel.OpponentRows, row => row.Name.Contains("Dribblet", StringComparison.OrdinalIgnoreCase));
                Assert.NotEmpty(viewModel.OpponentDamageRows);
                Assert.Equal(Visibility.Collapsed, viewModel.RecommendedPositionsVisibility);
                var changedOutcome = battle with { Outcome = new(false) };
                ((Task)typeof(OverlayViewModel).GetMethod("RefreshBattleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [changedOutcome])!).GetAwaiter().GetResult();
                Assert.Equal("LOSS", viewModel.BattleSummary);
                ((Task)typeof(OverlayViewModel).GetMethod("RefreshBattleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [battle])!).GetAwaiter().GetResult();
                Render((FrameworkElement)window.Content, 1300, 1000, "board-battle-ui.png");
                // New combat metadata must refresh advice even if the visible
                // cards have not changed, including when it cancels earlier work.
                var shopRaw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-pylong.json"));
                var exportedShop = System.Text.Json.JsonSerializer.Deserialize<GameStateMessage>(shopRaw, GameStateBridge.JsonOptions)!;
                var shop = GameStateBridge.Parse(shopRaw, catalog, exportedShop.UpdatedAt);
                var damageRefresh = typeof(OverlayViewModel).GetMethod("RefreshDamageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                using var cancellationExceptions = new CancellationExceptionTracker();
                var cancelled = (Task)damageRefresh.Invoke(viewModel, [shop.Board, shop.Combat])!;
                var latest = (Task)damageRefresh.Invoke(viewModel, [shop.Board, shop.Combat! with { Trinkets = ["haste-orb"] }])!;
                var adviceFrame = new System.Windows.Threading.DispatcherFrame();
                _ = latest.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => adviceFrame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(adviceFrame);
                latest.GetAwaiter().GetResult();
                Assert.Contains("haste-orb", (string?)typeof(OverlayViewModel).GetField("_positionIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
                Assert.DoesNotContain("Checking", viewModel.PositionHint);
                cancelled.GetAwaiter().GetResult();
                Assert.Equal(0, cancellationExceptions.Count);
                Assert.Equal(Dock.Top, tabs.TabStripPlacement);
                Assert.Equal(new[] { "Board", "Creatures", "Items", "Trinkets" }, tabs.Items.OfType<TabItem>().Select(tab => (string)tab.Header));
                Render((FrameworkElement)window.Content, 1440, 900, "dashboard-wide.png");
                var analysis = (FrameworkElement)window.FindName("BoardAnalysis");
                Assert.Equal(1, Grid.GetColumn(analysis));
                Render((FrameworkElement)window.Content, 960, 640, "dashboard-compact.png");
                Assert.Equal(0, Grid.GetColumn(analysis));
                Assert.Equal(1, Grid.GetRow(analysis));
                for (var page = 1; page < 4; page++)
                {
                    tabs.SelectedIndex = page;
                    Render((FrameworkElement)window.Content, 1440, 900, $"dashboard-page-{page}.png");
                    Render((FrameworkElement)window.Content, 960, 640, $"dashboard-page-{page}-compact.png");
                    Render((FrameworkElement)window.Content, 960, 640, $"dashboard-page-{page}-150.png", 1.5);
                    var grid = Descendants((FrameworkElement)window.Content).OfType<DataGrid>().Single();
                    Assert.Same(page switch { 1 => (object)viewModel.CreatureRows, 2 => viewModel.ItemRows, _ => viewModel.TrinketRows }, grid.ItemsSource);
                    Assert.True(grid.CanUserSortColumns);
                    var columns = grid.Columns.Select(column => column.Header?.ToString()).ToList();
                    var nameHeader = page switch { 1 => "Creature", 2 => "Item", _ => "Trinket" };
                    Assert.Equal(page == 1 ? "Ability" : "Effect", columns[columns.IndexOf(nameHeader) + 1]);
                    if (page == 1)
                    {
                        var creature = viewModel.CreatureRows.First(row => catalog.Monsters.Single(monster => monster.Id == row.Id).Rules?.Abilities.Count > 0);
                        var rules = catalog.Monsters.Single(monster => monster.Id == creature.Id).Rules!;
                        foreach (var ability in rules.Abilities.Values.Where(value => !string.IsNullOrWhiteSpace(value)))
                            Assert.Contains(ability, creature.Ability);
                    }
                }
                viewModel.SearchText = "no-such-creature-for-ui-test";
                Assert.Empty(viewModel.CreatureRows);
                tabs.SelectedIndex = 1;
                Render((FrameworkElement)window.Content, 960, 640, "dashboard-empty.png");
                Assert.Contains(Descendants((FrameworkElement)window.Content).OfType<TextBlock>(), text => text.Text.StartsWith("No results.") && text.Visibility == Visibility.Visible);
                viewModel.SearchText = "";
                tabs.SelectedIndex = 0;
                var review = new CaptureReviewWindow(result, catalog);
                var correction = (CaptureReviewViewModel)review.DataContext;
                var empty = correction.Fields.Single(field => field.Name == "Team[0].Occupant");
                empty.Value = "monster:scorchimp";
                Assert.Contains(correction.Fields, field => field.Name == "Team[0].Level");
                Assert.Contains(correction.Fields, field => field.Name == "Team[0].Variant");
                empty.AddBadgeCommand!.Execute(null);
                Assert.Contains(correction.Fields, field => field.Name == "Team[0].Stats[1].Kind");
                empty.DisplayValue = "Electranade";
                Assert.Equal("monster:galvanade", empty.Value);
                var kind = correction.Fields.Single(field => field.Name == "Team[0].Stats[0].Kind");
                Assert.Contains("Absent", kind.Candidates); Assert.Contains("Multicast", kind.Candidates);
                Render((FrameworkElement)review.Content, 1180, 780, "board-review-ui.png");
                var editor = Descendants((FrameworkElement)review.Content).OfType<ComboBox>().First();
                editor.ApplyTemplate();
                var text = (TextBox)editor.Template.FindName("PART_EditableTextBox", editor);
                Assert.True(Contrast(text.Foreground, text.Background) >= 4.5);
                Assert.True(Contrast(text.CaretBrush, text.Background) >= 4.5);
                Assert.True(Contrast(text.SelectionTextBrush, text.SelectionBrush) >= 4.5);
                text.Text = "38";
                Assert.Equal("38", editor.Text);
                Assert.Equal("38", correction.Fields[0].DisplayValue);
                // A presentation source is needed for real keyboard focus/caret
                // rendering. Keep the test window off screen and out of the taskbar.
                review.WindowStartupLocation = WindowStartupLocation.Manual;
                review.Left = -20000; review.Top = -20000; review.ShowInTaskbar = false; review.ShowActivated = false;
                review.Show(); text.Focus();
                Assert.True(editor.IsKeyboardFocusWithin);
                text.SelectAll();
                Assert.Equal(2, text.SelectionLength);
                review.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                Render((FrameworkElement)review.Content, 1180, 780, "board-review-focused.png");
                editor.IsDropDownOpen = true;
                var popup = (System.Windows.Controls.Primitives.Popup)editor.Template.FindName("PART_Popup", editor);
                Render((FrameworkElement)popup.Child, 450, 260, "board-review-dropdown.png");
                editor.SelectedIndex = 0;
                Assert.Equal(editor.Items[0].ToString(), correction.Fields[0].DisplayValue);
                editor.IsDropDownOpen = false; editor.IsEnabled = false;
                Render((FrameworkElement)review.Content, 1180, 780, "board-review-disabled.png");
                Assert.True(Contrast(text.Foreground, text.Background) >= 4.5);
                VerifyWindowBehavior(window);
                window.Close(); review.Close(); app.Shutdown(); completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completed.Task;
    }
    private static void VerifyWindowBehavior(MainWindow window)
    {
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        void ToggleVisibility() => SendMessage(hwnd, 0x0312, new IntPtr(0xB470), IntPtr.Zero);
        void Press(System.Windows.Input.Key key)
        {
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(window), Environment.TickCount, key)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(args);
            Assert.True(args.Handled);
        }
        ToggleVisibility();
        Assert.False(window.IsVisible);
        ToggleVisibility();
        Assert.True(window.IsVisible);
        var monitors = new List<IntPtr>();
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) => { monitors.Add(monitor); return true; };
        Assert.True(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero));
        foreach (var monitor in monitors)
        {
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            Assert.True(GetMonitorInfo(monitor, ref info));
            window.WindowState = WindowState.Normal;
            Assert.True(SetWindowPos(hwnd, IntPtr.Zero, info.Work.Left + 20, info.Work.Top + 20, 700, 500, 0x0014));
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var before = window.RestoreBounds;
            Press(System.Windows.Input.Key.F11);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(window.IsFullscreen);
            Assert.True(GetWindowRect(hwnd, out var bounds));
            Assert.Equal(info.Work.Left, bounds.Left);
            Assert.Equal(info.Work.Top, bounds.Top);
            Assert.Equal(info.Work.Right, bounds.Right);
            Assert.Equal(info.Work.Bottom, bounds.Bottom);
            var minimize = Descendants((FrameworkElement)window.Content).OfType<Button>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Minimize");
            minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WindowState.Minimized, window.WindowState);
            ToggleVisibility();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Press(System.Windows.Input.Key.Escape);
            Assert.False(window.IsFullscreen);
            Assert.Equal(before, window.RestoreBounds);
        }
        window.Hide();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i=0; i<VisualTreeHelper.GetChildrenCount(root); i++)
        { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var descendant in Descendants(child)) yield return descendant; }
    }
    private sealed class CancellationExceptionTracker : IDisposable
    {
        private readonly AsyncLocal<bool> _enabled = new();
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public CancellationExceptionTracker()
        {
            _enabled.Value = true;
            AppDomain.CurrentDomain.FirstChanceException += OnException;
        }
        private void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (_enabled.Value && args.Exception is OperationCanceledException) Interlocked.Increment(ref _count);
        }
        public void Dispose()
        {
            _enabled.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }
    }
    private static double Contrast(System.Windows.Media.Brush foreground, System.Windows.Media.Brush background)
    {
        var a=Luminance(((SolidColorBrush)foreground).Color); var b=Luminance(((SolidColorBrush)background).Color);
        return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
        static double Luminance(System.Windows.Media.Color c)
        { return .2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B); }
        static double Channel(byte value)
        { var s=value/255d; return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4); }
    }
    private static void Render(FrameworkElement content, int width, int height, string filename, double scale = 1)
    {
        content.Measure(new System.Windows.Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        // DataGrid resolves star column widths on the dispatcher after first layout.
        content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        content.Measure(new System.Windows.Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var target = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); target.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); encoder.Save(file);
    }
}
