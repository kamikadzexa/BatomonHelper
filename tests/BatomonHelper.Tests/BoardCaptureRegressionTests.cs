using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class BoardCaptureRegressionTests
{
    private static Task<CatalogSnapshot?> Catalog() => new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync();
    private static Bitmap Fixture(int day = 1) => new(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"board-day{day}.png"));
    private static void CopyRegion(Bitmap source, NormalizedRect from, Bitmap destination, NormalizedRect to)
    {
        using var graphics = Graphics.FromImage(destination); graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, to.ToPixels(destination.Width, destination.Height), from.ToPixels(source.Width, source.Height), GraphicsUnit.Pixel);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(640, 360)]
    public async Task Scaling_preserves_positions_and_never_auto_accepts_unread_fields(int width, int height)
    {
        using var original = Fixture(3); using var scaled = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(scaled))
        { graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half; graphics.DrawImage(original, new Rectangle(0, 0, width, height)); }
        var catalog = (await Catalog())!;
        var result = await new WindowsBoardScreenReader().RecognizeImageAsync(scaled, catalog);
        Assert.True(result.IsShop); Assert.Equal(6, result.Snapshot!.Team.Length);
        Assert.Equal(4, result.Snapshot.Bench.Length); Assert.Equal(5, result.Snapshot.Shop.Length);
        foreach (var field in result.Fields.Where(field => !field.IsResolved)) Assert.True(result.RequiresReview, field.Name);
        if (!result.RequiresReview) Assert.True(BoardSnapshotValidator.Validate(result.Snapshot, catalog).IsValid);
        Assert.Equal(3, result.Snapshot.Day); Assert.Equal(9, result.Snapshot.Lives); Assert.Equal(50, result.Snapshot.Gold);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recognizes_verified_black_bars(bool vertical)
    {
        using var original = Fixture();
        using var framed = new Bitmap(original.Width + (vertical ? 400 : 0), original.Height + (vertical ? 0 : 400));
        using (var graphics = Graphics.FromImage(framed))
        { graphics.Clear(Color.Black); graphics.DrawImage(original, new Rectangle(vertical ? 200 : 0, vertical ? 0 : 200, original.Width, original.Height), new Rectangle(0, 0, original.Width, original.Height), GraphicsUnit.Pixel); }
        var result = await new WindowsBoardScreenReader().RecognizeImageAsync(framed, (await Catalog())!);
        Assert.True(result.IsShop, string.Join(" ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.False(result.RequiresReview); Assert.Equal(300, result.Snapshot!.TeamHp);
        Assert.Equal(new CaptureContentBounds(framed.Width, framed.Height, vertical ? 200 : 0, vertical ? 0 : 200, original.Width, original.Height), result.MaterializeFrame().Frame!.ContentBounds);
    }

    [Fact]
    public async Task Multiple_shop_anchors_reject_other_screens()
    {
        using var image = new Bitmap(1600, 900); using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
        var result = await new WindowsBoardScreenReader().RecognizeImageAsync(image, (await Catalog())!);
        Assert.False(result.IsShop); Assert.Null(result.Snapshot); Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "not-shop");
    }

    [Fact]
    public async Task Occluded_values_and_cancelled_review_preserve_the_accepted_state()
    {
        var catalog = (await Catalog())!; using var image = Fixture();
        var reader = new WindowsBoardScreenReader(); var original = await reader.RecognizeImageAsync(image, catalog);
        var state = new BoardCaptureState(); var accepted = state.Publish(original, catalog);
        using (var graphics = Graphics.FromImage(image)) graphics.FillRectangle(Brushes.Black, BoardLayout.Gold.ToPixels(image.Width, image.Height));
        var proposal = await reader.RecognizeImageAsync(image, catalog);
        Assert.True(proposal.RequiresReview); Assert.Null(proposal.Snapshot!.Gold);
        Assert.Throws<InvalidOperationException>(() => state.Publish(proposal, catalog)); Assert.Same(accepted, state.Accepted);
        // Cancellation publishes nothing. Invalid corrections also leave both board and projection intact.
        Assert.Throws<FormatException>(() => BoardSnapshotCorrections.Apply(proposal.Snapshot, [new("Gold", "bad", 0, false, [])], catalog));
        Assert.Same(accepted, state.Accepted);
        var corrected = BoardSnapshotCorrections.Apply(proposal.Snapshot, [new("Gold", "25", 1, true, [])], catalog);
        var next = state.Publish(proposal, catalog, corrected); Assert.Equal(25, next.Board.Gold); Assert.Equal(25, next.ShopOdds.Gold);
    }

    [Fact]
    public async Task Price_and_stats_changes_invalidate_their_own_regions()
    {
        var catalog = (await Catalog())!; using var image = Fixture(); using var third = Fixture(3);
        var reader = new WindowsBoardScreenReader(); await reader.RecognizeImageAsync(image, catalog);
        CopyRegion(third, BoardLayout.Price(BoardLayout.Shop[1]), image, BoardLayout.Price(BoardLayout.Shop[0]));
        var changedPrice = await reader.RecognizeImageAsync(image, catalog);
        Assert.Equal(10, changedPrice.Snapshot!.Shop[0].Price); Assert.True(changedPrice.Timings.SpriteCacheHits > 0);
        CopyRegion(third, BoardLayout.Badges(BoardLayout.Team[5], false), image, BoardLayout.Badges(BoardLayout.Shop[1], true));
        var changedStats = await reader.RecognizeImageAsync(image, catalog);
        Assert.Equal("scorchimp", changedStats.Snapshot!.Shop[1].DefinitionId);
        Assert.Equal(new[] { new VisibleStat(VisibleStatKind.Damage, 15), new(VisibleStatKind.Burn, 10) }, changedStats.Snapshot.Shop[1].Stats.ToArray());
        Assert.True(changedStats.Timings.SpriteCacheHits > 0);
        using var second = Fixture(2); CopyRegion(second, BoardLayout.Gold, image, BoardLayout.Gold);
        var noGold = await reader.RecognizeImageAsync(image, catalog);
        Assert.False(noGold.Snapshot!.Shop[0].Affordable); Assert.True(noGold.Snapshot.Shop[4].Affordable);
    }

    [Fact]
    public async Task Catalog_and_geometry_changes_invalidate_recognition_caches()
    {
        var catalog = (await Catalog())!; using var image = Fixture(); var reader = new WindowsBoardScreenReader();
        await reader.RecognizeImageAsync(image, catalog);
        var refreshed = await reader.RecognizeImageAsync(image, catalog with { ContentHash = "different-assets" });
        Assert.Equal(0, refreshed.Timings.SpriteCacheHits);
        Assert.True(refreshed.Timings.OcrCalls > 0);
        using var scaled = new Bitmap(image, 1280, 720);
        var resized = await reader.RecognizeImageAsync(scaled, catalog with { ContentHash = "different-assets" });
        Assert.Equal(0, resized.Timings.SpriteCacheHits); Assert.True(resized.Timings.OcrCalls > 0);
    }

    [Fact]
    public async Task Shiny_matching_uses_separate_templates_and_the_complete_catalog()
    {
        var catalog = (await Catalog())!; using var image = Fixture();
        var spriteRect = BoardLayout.Sprite(BoardLayout.Shop[0], true).ToPixels(image.Width, image.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.FillRectangle(Brushes.White, spriteRect);
            using var shiny = new Bitmap(Path.Combine(AppContext.BaseDirectory, catalog.Monsters.Single(monster => monster.Id == "joltail").ShinySpritePath!));
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var scale = image.Width / 640d;
            graphics.DrawImage(shiny, new Rectangle(spriteRect.X + (int)Math.Round((spriteRect.Width - 48 * scale) / 2), spriteRect.Y + (int)Math.Round(spriteRect.Height - 48 * scale + 5 * scale), (int)Math.Round(48 * scale), (int)Math.Round(48 * scale)));
        }
        var result = await new WindowsBoardScreenReader().RecognizeImageAsync(image, catalog);
        Assert.Equal("joltail", result.Snapshot!.Shop[0].DefinitionId); Assert.Equal(ShinyState.Shiny, result.Snapshot.Shop[0].Variant);
    }

    [Fact]
    public async Task Burst_presses_coalesce_into_one_pending_refresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int count = 0;
        var queue = new CaptureRefreshQueue(async _ => { if (Interlocked.Increment(ref count) == 1) { entered.SetResult(); await release.Task; } });
        var first = queue.RequestAsync(); await entered.Task;
        var requests = Enumerable.Range(0, 20).Select(_ => queue.RequestAsync()).ToArray();
        release.SetResult(); await Task.WhenAll(requests.Append(first)); Assert.Equal(2, count);
    }

    [Fact]
    public async Task Cancellation_stops_the_pending_refresh()
    {
        using var cancellation = new CancellationTokenSource(); int count = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new CaptureRefreshQueue(async token => { count++; entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }, cancellation.Token);
        var first = queue.RequestAsync(); await entered.Task; _ = queue.RequestAsync(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.Equal(1, count);
    }

    [Fact]
    public async Task Corrections_validate_odds_ids_variants_and_absent_versus_zero_stats()
    {
        var catalog = (await Catalog())!; using var image = Fixture();
        var board = (await new WindowsBoardScreenReader().RecognizeImageAsync(image, catalog)).Snapshot!;
        Assert.Throws<FormatException>(() => BoardSnapshotCorrections.Apply(board, [new("CommonOdds", "garbage", 1, true, [])], catalog));
        Assert.Throws<FormatException>(() => BoardSnapshotCorrections.Apply(board, [new("CommonOdds", "90", 1, true, [])], catalog));
        Assert.Throws<FormatException>(() => BoardSnapshotCorrections.Apply(board, [new("Shop[0].Variant", "Unknown", 1, true, [])], catalog));
        Assert.Throws<FormatException>(() => BoardSnapshotCorrections.Apply(board, [new("Shop[0].Occupant", "monster:missing", 1, true, [])], catalog));
        var zero = BoardSnapshotCorrections.Apply(board, [new("Shop[0].Stats[0].Kind", "CooldownSpeed", 1, true, []), new("Shop[0].Stats[0].Value", "0", 1, true, [])], catalog);
        Assert.Equal(new VisibleStat(VisibleStatKind.CooldownSpeed, 0), Assert.Single(zero.Shop[0].Stats));
        var absent = BoardSnapshotCorrections.Apply(board, [new("Shop[0].Stats[0].Kind", "Absent", 1, true, [])], catalog);
        Assert.Empty(absent.Shop[0].Stats);
        var paid = BoardSnapshotCorrections.Apply(board, [new("RerollMode", "Paid", 1, true, []), new("RerollCount", "3", 1, true, [])], catalog);
        Assert.Equal(3, paid.RerollCost); Assert.Null(paid.FreeRerolls);
    }
}
