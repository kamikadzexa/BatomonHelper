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
        Assert.Equal(expectedSnapshot.GetProperty("Trainer").GetString(), result.Snapshot!.Trainer);
        Assert.Equal(expectedSnapshot.GetProperty("ShopRank").GetInt32(), result.Snapshot.ShopRank);
        Assert.Equal(expectedSnapshot.GetProperty("Gold").GetInt32(), result.Snapshot.Gold);
        Assert.Equal([100, 0, 0, 0, 0], result.Snapshot.RarityOdds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability * 100m));
        Assert.Equal(expectedSnapshot.GetProperty("ItemUsesRemaining").GetInt32(), result.Snapshot.ItemUsesRemaining);
        Assert.Equal(expectedSnapshot.GetProperty("DailyItemLimit").GetInt32(), result.Snapshot.DailyItemLimit);
        Assert.Equal(expectedSnapshot.GetProperty("ItemOffer").GetProperty("Name").GetString(), result.Snapshot.ItemOffer!.Name);
        Assert.Equal(expectedSnapshot.GetProperty("ItemOffer").GetProperty("Price").GetInt32(), result.Snapshot.ItemOffer.Price);
        Assert.All(result.Fields, field => Assert.True(field.IsResolved, field.Name));
    }

    private static CatalogSnapshot Catalog() => new("test", "25377295", "22", DateTimeOffset.UtcNow, [], "hash", [],
        [new MonsterDefinition("bunchop", "Bunchop", Rarity.Common, null, ImmutableHashSet.Create("starter"))])
    {
        Trainers = [new("lucky-girl", "Lucky Girl", "Shiny monsters are more likely to appear.")],
        Items = [new("green-stone", "Green Stone", Price: 0)]
    };
}
