using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class OddsCalculatorTests
{
    private readonly OddsCalculator _calculator = new();

    [Fact]
    public void Captured_common_ribbon_odds_split_between_eligible_common_monsters()
    {
        var result = _calculator.Calculate(Catalog(), Snapshot());

        Assert.Equal(1m, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).Probability);
        Assert.Equal(.5m, result.MonsterOdds.Single(x => x.MonsterId == "alpha").PerBatomonRoll);
        Assert.Equal(.96875m, result.MonsterOdds.Single(x => x.MonsterId == "alpha").NextShopChance);
    }

    [Fact]
    public void Uses_the_captured_ribbon_without_recalculating_item_effects()
    {
        var snapshot = Snapshot() with
        {
            RarityOdds = Odds((Rarity.Common, .67m), (Rarity.Uncommon, .30m), (Rarity.Rare, .03m), (Rarity.SuperRare, 0m), (Rarity.Legendary, 0m)),
            ItemUsesRemaining = 1,
            ItemOffer = new("Gray Chip", 0)
        };
        var result = _calculator.Calculate(Catalog(), snapshot);

        Assert.Equal(.03m, result.RarityOdds.Single(x => x.Rarity == Rarity.Rare).Probability);
        Assert.Equal(4, result.BatomonSlotsNextShop);
    }

    [Fact]
    public void Five_batomon_slots_apply_when_no_item_use_remains()
    {
        var result = _calculator.Calculate(Catalog(), Snapshot() with { ItemUsesRemaining = 0, ItemOffer = null });

        Assert.Equal(5, result.BatomonSlotsNextShop);
    }

    [Fact]
    public void User_selected_region_limits_the_eligible_pool()
    {
        var catalog = new CatalogSnapshot("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("alpha", Rarity.Common, "Jinto region"), Monster("beta", Rarity.Common, "Pantra region"), Monster("both", Rarity.Common, "Jinto region", "Pantra region")
        ]);
        var snapshot = Snapshot() with { RegionFilter = MonsterRegionFilter.Jinto };
        var result = _calculator.Calculate(catalog, snapshot);

        Assert.DoesNotContain(result.MonsterOdds, x => x.MonsterId == "beta");
        Assert.Equal(2, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).EligibleCount);
    }

    private static CatalogSnapshot Catalog() => new("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("alpha", Rarity.Common), Monster("beta", Rarity.Common), Monster("gamma", Rarity.Uncommon),
            Monster("delta", Rarity.Rare), Monster("epsilon", Rarity.SuperRare), Monster("zeta", Rarity.Legendary), Monster("myth", Rarity.Mythical)
        ]);

    private static MonsterDefinition Monster(string id, Rarity rarity, params string[] regions) => new(id, id, rarity, null, ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "starter")) { Regions = regions.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase) };
    private static ShopOddsSnapshot Snapshot() => new("Black Belt", 4, 45, Odds((Rarity.Common, 1m), (Rarity.Uncommon, 0m), (Rarity.Rare, 0m), (Rarity.SuperRare, 0m), (Rarity.Legendary, 0m)), 0, 1, null, DateTimeOffset.UtcNow);
    private static ImmutableArray<CapturedRarityOdds> Odds(params (Rarity Rarity, decimal Probability)[] values) => values.Select(value => new CapturedRarityOdds(value.Rarity, value.Probability)).ToImmutableArray();
}
