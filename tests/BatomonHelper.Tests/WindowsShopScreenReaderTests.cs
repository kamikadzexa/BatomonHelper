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

        var result = await new WindowsShopScreenReader().RecognizeImageAsync(image, Catalog());

        Assert.NotNull(result.Snapshot);
        Assert.Equal(11, result.Snapshot!.ShopRank);
        Assert.Equal(124, result.Snapshot.Gold);
        Assert.Equal([10, 20, 35, 25, 10], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.True(result.Snapshot.ItemAvailable);
        Assert.True(result.Fields.Single(field => field.Name == "ShopRank").IsResolved);
        Assert.Equal(new CaptureContentBounds(2434, 1369, 0, 0, 2434, 1369), result.Frame!.ContentBounds);
    }

    [Fact]
    public async Task Recognizes_rank_eleven_after_a_small_resolution_and_aspect_change()
    {
        using var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rank11-shop.png"));
        using var resized = new Bitmap(1217, 686);
        using (var graphics = Graphics.FromImage(resized))
            graphics.DrawImage(original, new Rectangle(0, 0, resized.Width, resized.Height));

        var result = await new WindowsShopScreenReader().RecognizeImageAsync(resized, Catalog());

        Assert.NotNull(result.Snapshot);
        Assert.Equal(11, result.Snapshot!.ShopRank);
        Assert.Equal(124, result.Snapshot.Gold);
        Assert.Equal([10, 20, 35, 25, 10], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.Equal(new CaptureContentBounds(1217, 686, 0, 0, 1217, 686), result.Frame!.ContentBounds);
    }

    [Fact]
    public async Task Recognizes_the_supplied_lucky_girl_shop_capture()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "lucky-girl-rank1.png");
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lucky-girl-rank1.expected.json")));
        var expectedSnapshot = expected.RootElement.GetProperty("Result").GetProperty("Snapshot");
        using var image = new Bitmap(fixture);
        var result = await new WindowsShopScreenReader().RecognizeImageAsync(image, Catalog());

        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot!.Trainer);
        Assert.Equal(expectedSnapshot.GetProperty("ShopRank").GetInt32(), result.Snapshot.ShopRank);
        Assert.Equal(expectedSnapshot.GetProperty("Gold").GetInt32(), result.Snapshot.Gold);
        Assert.Equal([100, 0, 0, 0, 0], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.True(result.Snapshot.ItemAvailable);
        Assert.Null(result.Snapshot.ItemOffer);
        Assert.All(result.Fields, field => Assert.True(field.IsResolved, field.Name));
    }

    [Fact]
    public async Task Marks_unread_legacy_ribbon_as_an_editable_rank_table_fallback()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "treasure-hunter-rank9.png");
        using var image = new Bitmap(fixture);
        var catalog = new CatalogSnapshot("test", "25377295", "22", DateTimeOffset.UtcNow, [], "hash", [], [])
        {
            Trainers = [new("treasure-hunter", "Treasure Hunter", "When you get Trinket gifts, your choices are 1 rarity tier higher.")],
            Items = [new("gray-ticket", "Gray Ticket", "Reroll a shop with all Common monsters.", 0)]
        };

        var result = await new WindowsShopScreenReader().RecognizeImageAsync(image, catalog);

        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot!.Trainer);
        Assert.Equal(3, result.Snapshot.ShopRank);
        Assert.Equal(22, result.Snapshot.Gold);
        Assert.Equal([75, 25, 0, 0, 0], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.True(result.Snapshot.ItemAvailable);
        Assert.Null(result.Snapshot.ItemOffer);
        Assert.Contains(result.Fields, field => field.Name == "ShopRank" && field.IsResolved);
        Assert.All(result.Fields.Where(field => field.Name.EndsWith("Odds", StringComparison.Ordinal)), field => Assert.False(field.IsResolved));
    }

    private static CatalogSnapshot Catalog() => new("test", "25377295", "22", DateTimeOffset.UtcNow, [], "hash", [],
        [new MonsterDefinition("bunchop", "Bunchop", Rarity.Common, null, ImmutableHashSet.Create("starter"))])
    {
        Trainers = [new("lucky-girl", "Lucky Girl", "Shiny monsters are more likely to appear.")],
        Items = [new("green-stone", "Green Stone", Price: 0)]
    };
}
