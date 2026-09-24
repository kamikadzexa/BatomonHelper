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
            ItemAvailable = true,
            ItemOffer = new("Gray Chip", 0)
        };
        var result = _calculator.Calculate(Catalog(), snapshot);

        Assert.Equal(.03m, result.RarityOdds.Single(x => x.Rarity == Rarity.Rare).Probability);
        Assert.Equal(4, result.BatomonSlotsNextShop);
    }

    [Fact]
    public void Five_batomon_slots_apply_when_no_item_use_remains()
    {
        var result = _calculator.Calculate(Catalog(), Snapshot() with { ItemAvailable = false, ItemOffer = null });

        Assert.Equal(5, result.BatomonSlotsNextShop);
    }

    [Fact]
    public void Item_odds_split_the_captured_ribbon_between_items_of_the_same_rarity()
    {
        var catalog = Catalog() with
        {
            Items = [new("chip-a", "Chip A", Rarity: Rarity.Common), new("chip-b", "Chip B", Rarity: Rarity.Common)]
        };
        var snapshot = Snapshot() with { ItemAvailable = true };

        var result = _calculator.Calculate(catalog, snapshot);

        Assert.All(result.ItemOdds, odds =>
        {
            Assert.Equal(.5m, odds.PerItemRoll);
            Assert.Equal(.5m, odds.NextShopChance);
        });
    }

    [Fact]
    public void Item_odds_are_zero_when_the_shop_has_no_item_slot()
    {
        var catalog = Catalog() with { Items = [new("chip", "Chip", Rarity: Rarity.Common)] };

        var result = _calculator.Calculate(catalog, Snapshot() with { ItemAvailable = false });

        Assert.Equal(0m, Assert.Single(result.ItemOdds).PerItemRoll);
    }

    [Fact]
    public void Level_three_creatures_are_removed_from_their_rarity_pool()
    {
        var result = _calculator.Calculate(Catalog(), Snapshot(), new(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha" }));

        Assert.DoesNotContain(result.MonsterOdds, odds => odds.MonsterId == "alpha");
        Assert.Equal(1, result.RarityOdds.Single(odds => odds.Rarity == Rarity.Common).EligibleCount);
        Assert.Equal(1m, result.MonsterOdds.Single(odds => odds.MonsterId == "beta").PerBatomonRoll);
    }

    [Fact]
    public void Trinket_chance_uses_a_separate_pool_for_each_rarity_and_excludes_owned_uniques()
    {
        var catalog = Catalog() with
        {
            Trinkets = [
                new("common-a", "Common A", Rarity.Common),
                new("common-unique", "Common Unique", Rarity.Common, IsUnique: true),
                new("rare-a", "Rare A", Rarity.Rare)
            ]
        };
        var snapshot = Snapshot() with { RarityOdds = Odds((Rarity.Common, .8m), (Rarity.Uncommon, 0m), (Rarity.Rare, .2m), (Rarity.SuperRare, 0m), (Rarity.Legendary, 0m)) };

        var result = _calculator.Calculate(catalog, snapshot, new(OwnedUniqueTrinketIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "common-unique" }));

        var common = result.TrinketOdds.Single(odds => odds.TrinketId == "common-a");
        var owned = result.TrinketOdds.Single(odds => odds.TrinketId == "common-unique");
        Assert.Equal(.8m, common.PerTrinketRoll);
        Assert.Equal(1, common.EligibleCount);
        Assert.False(owned.IsEligible);
        Assert.Equal(0m, owned.PerTrinketRoll);
        Assert.Equal(.2m, result.TrinketOdds.Single(odds => odds.TrinketId == "rare-a").PerTrinketRoll);
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

    [Fact]
    public void Evolution_forms_do_not_dilute_their_base_form_shop_chance()
    {
        var catalog = new CatalogSnapshot("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("base", Rarity.Common), Evolution("evolved", Rarity.Common, "base"), Monster("other", Rarity.Common)
        ]);

        var result = _calculator.Calculate(catalog, Snapshot());

        Assert.Equal(2, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).EligibleCount);
        Assert.Equal(.5m, result.MonsterOdds.Single(x => x.MonsterId == "base").PerBatomonRoll);
        Assert.DoesNotContain(result.MonsterOdds, x => x.MonsterId == "evolved");
    }

    [Fact]
    public void Shop_odds_include_available_creatures_even_when_the_catalog_has_no_active_set_metadata()
    {
        var catalog = new CatalogSnapshot("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("available", Rarity.Common, "Jinto region") with { ActiveSets = [] },
            Monster("starter", Rarity.Common, "Jinto region")
        ]);

        var result = _calculator.Calculate(catalog, Snapshot() with { RegionFilter = MonsterRegionFilter.Jinto });

        Assert.Equal(2, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).EligibleCount);
        Assert.All(result.MonsterOdds, odds => Assert.Equal(.5m, odds.PerBatomonRoll));
    }

    [Theory]
    [InlineData("ignit")]
    [InlineData("talonite")]
    [InlineData("wishwash")]
    [InlineData("purple-egg")]
    public void Shop_odds_exclude_unavailable_creatures_and_eggs(string id)
    {
        var catalog = new CatalogSnapshot("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [Monster("available", Rarity.Common), Monster(id, Rarity.Common)]);

        var result = _calculator.Calculate(catalog, Snapshot());

        Assert.Equal(1, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).EligibleCount);
        Assert.Equal(["available"], result.MonsterOdds.Select(odds => odds.MonsterId));
    }

    private static CatalogSnapshot Catalog() => new("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("alpha", Rarity.Common), Monster("beta", Rarity.Common), Monster("gamma", Rarity.Uncommon),
            Monster("delta", Rarity.Rare), Monster("epsilon", Rarity.SuperRare), Monster("zeta", Rarity.Legendary), Monster("myth", Rarity.Mythical)
        ]);

    private static MonsterDefinition Monster(string id, Rarity rarity, params string[] regions) => new(id, id, rarity, null, ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "starter")) { Regions = regions.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase) };
    private static MonsterDefinition Evolution(string id, Rarity rarity, string baseSpeciesId) => new(id, id, rarity, baseSpeciesId, ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "starter"));
    private static ShopOddsSnapshot Snapshot() => new("Black Belt", 4, 45, Odds((Rarity.Common, 1m), (Rarity.Uncommon, 0m), (Rarity.Rare, 0m), (Rarity.SuperRare, 0m), (Rarity.Legendary, 0m)), false, null, DateTimeOffset.UtcNow);
    private static ImmutableArray<CapturedRarityOdds> Odds(params (Rarity Rarity, decimal Probability)[] values) => values.Select(value => new CapturedRarityOdds(value.Rarity, value.Probability)).ToImmutableArray();
}
