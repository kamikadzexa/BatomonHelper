using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.Drawing;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class WindowsShopScreenReaderTests
{
    [Fact]
    public async Task Recognizes_the_supplied_rank_eleven_shop_capture()
    {
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rank11-shop.png"));

        var result = await new WindowsShopScreenReader().RecognizeImageAsync(image, await Catalog());

        Assert.NotNull(result.Snapshot);
        Assert.Equal(11, result.Snapshot!.ShopRank);
        Assert.Equal(124, result.Snapshot.Gold);
        Assert.Equal([10, 20, 35, 25, 10], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.True(result.Snapshot.ItemAvailable);
        Assert.True(result.Fields.Single(field => field.Name == "ShopRank").IsResolved);
        Assert.Equal(new CaptureContentBounds(2434, 1369, 0, 0, 2434, 1369), result.Frame!.ContentBounds);
    }

    [Fact]
    public async Task Softly_scaled_ribbon_requires_correction_without_inventing_rank_table_values()
    {
        using var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rank11-shop.png"));
        using var resized = new Bitmap(1217, 686);
        using (var graphics = Graphics.FromImage(resized))
            graphics.DrawImage(original, new Rectangle(0, 0, resized.Width, resized.Height));

        var result = await new WindowsShopScreenReader().RecognizeImageAsync(resized, await Catalog());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "soft-rank.actual.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        Assert.NotNull(result.Snapshot);
        Assert.Equal(11, result.Snapshot!.ShopRank);
        Assert.Equal(124, result.Snapshot.Gold);
        Assert.True(result.Snapshot.RarityOdds.Length < 5);
        Assert.All(result.Fields.Where(field => field.Name.EndsWith("Odds", StringComparison.Ordinal)), field => Assert.False(field.IsResolved));
        Assert.Equal(new CaptureContentBounds(1217, 686, 0, 0, 1217, 686), result.Frame!.ContentBounds);
    }

    [Fact]
    public async Task Recognizes_the_supplied_lucky_girl_shop_capture()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "lucky-girl-rank1.png");
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lucky-girl-rank1.expected.json")));
        var expectedSnapshot = expected.RootElement.GetProperty("Result").GetProperty("Snapshot");
        using var image = new Bitmap(fixture);
        var result = await new WindowsShopScreenReader().RecognizeImageAsync(image, await Catalog());

        Assert.NotNull(result.Snapshot);
        Assert.Equal("Lucky Girl", result.Snapshot!.Trainer);
        Assert.Equal(expectedSnapshot.GetProperty("ShopRank").GetInt32(), result.Snapshot.ShopRank);
        Assert.Equal(expectedSnapshot.GetProperty("Gold").GetInt32(), result.Snapshot.Gold);
        Assert.Equal([100, 0, 0, 0, 0], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.True(result.Snapshot.ItemAvailable);
        Assert.Equal("Green Stone", result.Snapshot.ItemOffer!.Name);
        Assert.All(result.Fields.Where(field => field.Name.EndsWith("Odds", StringComparison.Ordinal)), field => Assert.True(field.IsResolved, field.Name));
    }

    [Fact]
    public async Task Reads_the_actual_legacy_ribbon_trainer_and_item()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "treasure-hunter-rank9.png");
        using var image = new Bitmap(fixture);
        var catalog = await Catalog();

        var result = await new WindowsShopScreenReader().RecognizeImageAsync(image, catalog);

        Assert.NotNull(result.Snapshot);
        Assert.Equal("Treasure Hunter", result.Snapshot!.Trainer);
        Assert.Equal(9, result.Snapshot.ShopRank);
        Assert.Equal(22, result.Snapshot.Gold);
        Assert.Equal([20, 25, 38, 15, 2], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.True(result.Snapshot.ItemAvailable, string.Join("\n", result.Fields.Where(field => !field.IsResolved).Select(field => $"{field.Name}: {field.Value}")));
        Assert.Equal("Gray Ticket", result.Snapshot.ItemOffer!.Name);
        Assert.Contains(result.Fields, field => field.Name == "ShopRank" && field.IsResolved);
        Assert.All(result.Fields.Where(field => field.Name.EndsWith("Odds", StringComparison.Ordinal)), field => Assert.True(field.IsResolved));
    }

    private static async Task<CatalogSnapshot> Catalog() => (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
}
