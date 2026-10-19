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
                var window = new MainWindow { Width = 1300, Height = 1000 };
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
                var cancelled = (Task)damageRefresh.Invoke(viewModel, [shop.Board, shop.Combat])!;
                var latest = (Task)damageRefresh.Invoke(viewModel, [shop.Board, shop.Combat! with { Trinkets = ["haste-orb"] }])!;
                var adviceFrame = new System.Windows.Threading.DispatcherFrame();
                _ = latest.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => adviceFrame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(adviceFrame);
                latest.GetAwaiter().GetResult();
                Assert.Contains("haste-orb", (string?)typeof(OverlayViewModel).GetField("_positionIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
                Assert.DoesNotContain("Checking", viewModel.PositionHint);
                cancelled.GetAwaiter().GetResult();
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
                window.Close(); review.Close(); app.Shutdown(); completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completed.Task;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i=0; i<VisualTreeHelper.GetChildrenCount(root); i++)
        { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var descendant in Descendants(child)) yield return descendant; }
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
    private static void Render(FrameworkElement content, int width, int height, string filename)
    {
        content.Measure(new System.Windows.Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); encoder.Save(file);
    }
}
