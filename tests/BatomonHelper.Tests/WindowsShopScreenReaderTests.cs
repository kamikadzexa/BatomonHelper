using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.Drawing;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class WindowsShopScreenReaderTests
{
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
        Assert.Equal(expectedSnapshot.GetProperty("ItemUsesRemaining").GetInt32(), result.Snapshot.ItemUsesRemaining);
        Assert.Equal(expectedSnapshot.GetProperty("DailyItemLimit").GetInt32(), result.Snapshot.DailyItemLimit);
        Assert.Null(result.Snapshot.ItemOffer);
        Assert.All(result.Fields, field => Assert.True(field.IsResolved, field.Name));
    }

    [Fact]
    public async Task Recognizes_the_supplied_treasure_hunter_rank9_shop_capture()
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
        Assert.Equal(9, result.Snapshot.ShopRank);
        Assert.Equal(22, result.Snapshot.Gold);
        Assert.Equal([20, 25, 38, 15, 2], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.Equal(2, result.Snapshot.ItemUsesRemaining);
        Assert.Equal(3, result.Snapshot.DailyItemLimit);
        Assert.Null(result.Snapshot.ItemOffer);
        Assert.All(result.Fields, field => Assert.True(field.IsResolved, field.Name));
    }

    private static CatalogSnapshot Catalog() => new("test", "25377295", "22", DateTimeOffset.UtcNow, [], "hash", [],
        [new MonsterDefinition("bunchop", "Bunchop", Rarity.Common, null, ImmutableHashSet.Create("starter"))])
    {
        Trainers = [new("lucky-girl", "Lucky Girl", "Shiny monsters are more likely to appear.")],
        Items = [new("green-stone", "Green Stone", Price: 0)]
    };
}
