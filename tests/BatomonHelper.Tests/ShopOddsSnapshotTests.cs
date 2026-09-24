using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ShopOddsSnapshotTests
{
    [Fact]
    public void Validates_the_five_ordered_raw_ribbon_odds()
    {
        var snapshot = FixtureSnapshot();

        Assert.True(ShopOddsSnapshotValidator.Validate(snapshot, CatalogSnapshot.Empty).IsValid);
        Assert.Equal(.67m, snapshot.RarityOdds.Single(odds => odds.Rarity == Rarity.Common).Probability);
        Assert.Equal(.30m, snapshot.RarityOdds.Single(odds => odds.Rarity == Rarity.Uncommon).Probability);
    }

    [Fact]
    public void Rejects_a_ribbon_that_does_not_total_one_hundred_percent()
    {
        var source = FixtureSnapshot();
        var snapshot = source with { RarityOdds = source.RarityOdds.SetItem(0, new(Rarity.Common, .66m)) };

        Assert.False(ShopOddsSnapshotValidator.Validate(snapshot, CatalogSnapshot.Empty).IsValid);
    }

    [Fact]
    public void Does_not_require_item_identity_while_an_item_use_remains()
    {
        var snapshot = FixtureSnapshot() with { ItemOffer = null };

        Assert.True(ShopOddsSnapshotValidator.Validate(snapshot, CatalogSnapshot.Empty).IsValid);
    }

    [Fact]
    public void Supplied_rank_four_fixture_has_the_expected_shop_odds_values()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shop-odds-rank4.expected.json")));
        var root = document.RootElement;

        Assert.Equal("Black Belt", root.GetProperty("trainer").GetString());
        Assert.Equal(4, root.GetProperty("shopRank").GetInt32());
        Assert.Equal(45, root.GetProperty("gold").GetInt32());
        Assert.Equal(67, root.GetProperty("rarityOdds").GetProperty("common").GetInt32());
        Assert.Equal("Gray Chip", root.GetProperty("shopItemOffer").GetProperty("name").GetString());
    }

    [Fact]
    public void Manual_rank_correction_updates_the_standard_ribbon()
    {
        var fields = new[] { new RecognizedField("CommonOdds", "90", 1, true, []), new RecognizedField("UncommonOdds", "10", 1, true, []), new RecognizedField("RareOdds", "0", 1, true, []), new RecognizedField("SuperRareOdds", "0", 1, true, []), new RecognizedField("LegendaryOdds", "0", 1, true, []) };

        var corrected = ShopOddsSnapshotCorrections.Apply(FixtureSnapshot(), fields, CatalogSnapshot.Empty);

        Assert.Equal(.9m, corrected.RarityOdds.Single(odds => odds.Rarity == Rarity.Common).Probability);
        Assert.Equal(.1m, corrected.RarityOdds.Single(odds => odds.Rarity == Rarity.Uncommon).Probability);
    }

    private static ShopOddsSnapshot FixtureSnapshot() => new("Black Belt", 4, 45,
        [new(Rarity.Common, .67m), new(Rarity.Uncommon, .30m), new(Rarity.Rare, .03m), new(Rarity.SuperRare, 0m), new(Rarity.Legendary, 0m)],
        1, 1, new("Gray Chip", 0), DateTimeOffset.UtcNow);
}
