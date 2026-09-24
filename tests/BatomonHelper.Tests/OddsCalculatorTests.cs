using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class OddsCalculatorTests
{
    private readonly OddsCalculator _calculator = new();

    [Fact]
    public void RankOne_splits_the_common_roll_between_eligible_common_monsters()
    {
        var result = _calculator.Calculate(Catalog(), State());

        Assert.Equal(1m, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).Probability);
        Assert.Equal(0.5m, result.MonsterOdds.Single(x => x.MonsterId == "alpha").PerBatomonRoll);
        Assert.Equal(0.96875m, result.MonsterOdds.Single(x => x.MonsterId == "alpha").NextShopChance);
    }

    [Fact]
    public void Research_notes_add_weight_before_normalizing()
    {
        var state = State() with { ShopRank = 9, Effects = new RunEffects(ResearchNotes: 1), ItemUsesRemaining = 1, HasEligibleItemOffer = true };
        var result = _calculator.Calculate(Catalog(), state);

        Assert.Equal(22m / 120m, result.RarityOdds.Single(x => x.Rarity == Rarity.Legendary).Probability);
        Assert.Equal(4, result.BatomonSlotsNextShop);
    }

    [Fact]
    public void Level_three_monster_is_excluded_from_normal_rolls()
    {
        var state = State() with { Team = [new UnitState("alpha", 3, false, 0, [], ImmutableDictionary<string, int>.Empty)] };
        var result = _calculator.Calculate(Catalog(), state);

        Assert.DoesNotContain(result.MonsterOdds, x => x.MonsterId == "alpha");
        Assert.Equal(1, result.RarityOdds.Single(x => x.Rarity == Rarity.Common).EligibleCount);
    }

    [Fact]
    public void Ticket_forces_all_five_batomon_slots()
    {
        var result = _calculator.Calculate(Catalog(), State() with { Effects = new RunEffects(TicketRarity: Rarity.Legendary), ItemUsesRemaining = 1, HasEligibleItemOffer = true });

        Assert.Equal(5, result.BatomonSlotsNextShop);
        Assert.Equal(1m, result.RarityOdds.Single(x => x.Rarity == Rarity.Legendary).Probability);
    }

    private static CatalogSnapshot Catalog() => new("test", "1", "1", DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("alpha", Rarity.Common), Monster("beta", Rarity.Common), Monster("gamma", Rarity.Uncommon),
            Monster("delta", Rarity.Rare), Monster("epsilon", Rarity.SuperRare), Monster("zeta", Rarity.Legendary), Monster("myth", Rarity.Mythical)
        ]);

    private static MonsterDefinition Monster(string id, Rarity rarity) => new(id, id, rarity, null, ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "starter"));
    private static RunState State() => RunState.Empty with { ShopRank = 1, ActiveSet = "starter" };
}
