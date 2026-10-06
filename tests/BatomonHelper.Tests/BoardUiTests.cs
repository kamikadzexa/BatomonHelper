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
                typeof(OverlayViewModel).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, catalog);
                typeof(OverlayViewModel).GetMethod("RenderBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [result.Snapshot]);
                Assert.Equal(6, viewModel.TeamRows.Count); Assert.Equal(4, viewModel.BenchRows.Count); Assert.Equal(5, viewModel.OfferRows.Count);
                Assert.Contains("Lv. 2", viewModel.TeamRows[5].Level); Assert.Contains("Damage 15", viewModel.TeamRows[5].Stats);
                Render((FrameworkElement)window.Content, 1300, 1000, "board-ui.png");
                var review = new CaptureReviewWindow(result, catalog);
                var correction = (CaptureReviewViewModel)review.DataContext;
                var empty = correction.Fields.Single(field => field.Name == "Team[0].Occupant");
                empty.Value = "monster:scorchimp";
                Assert.Contains(correction.Fields, field => field.Name == "Team[0].Level");
                Assert.Contains(correction.Fields, field => field.Name == "Team[0].Variant");
                empty.AddBadgeCommand!.Execute(null);
                Assert.Contains(correction.Fields, field => field.Name == "Team[0].Stats[1].Kind");
                var kind = correction.Fields.Single(field => field.Name == "Team[0].Stats[0].Kind");
                Assert.Contains("Absent", kind.Candidates); Assert.Contains("Multicast", kind.Candidates);
                Render((FrameworkElement)review.Content, 1180, 780, "board-review-ui.png");
                window.Close(); review.Close(); app.Shutdown(); completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completed.Task;
    }
    private static void Render(FrameworkElement content, int width, int height, string filename)
    {
        content.Measure(new System.Windows.Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); encoder.Save(file);
    }
}
