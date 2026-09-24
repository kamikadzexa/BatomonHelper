using System.Collections.Immutable;

namespace BatomonHelper.Core;

public sealed record RarityOdds(Rarity Rarity, decimal Probability, int EligibleCount, Rarity RolledAs);
public sealed record MonsterOdds(string MonsterId, string Name, Rarity Rarity, decimal PerBatomonRoll, decimal NextShopChance, string Explanation);
public sealed record OddsResult(
    ImmutableArray<RarityOdds> RarityOdds,
    ImmutableArray<MonsterOdds> MonsterOdds,
    int BatomonSlotsNextShop,
    ImmutableArray<string> Notes);

public interface IOddsCalculator
{
    OddsResult Calculate(CatalogSnapshot catalog, ShopOddsInput state);
}

public sealed class OddsCalculator : IOddsCalculator
{
    private static readonly IReadOnlyDictionary<int, decimal[]> BaseWeights = new Dictionary<int, decimal[]>
    {
        [1] = [100, 0, 0, 0, 0], [2] = [90, 10, 0, 0, 0], [3] = [75, 25, 0, 0, 0],
        [4] = [67, 30, 3, 0, 0], [5] = [55, 30, 15, 0, 0], [6] = [45, 32, 20, 3, 0],
        [7] = [30, 40, 25, 5, 0], [8] = [25, 30, 35, 10, 0], [9] = [20, 25, 38, 15, 2],
        [10] = [15, 20, 40, 20, 5], [11] = [10, 20, 35, 25, 10], [12] = [5, 15, 30, 35, 15],
        [13] = [1, 10, 30, 39, 20], [14] = [1, 2, 20, 47, 30]
    };

    public OddsResult Calculate(CatalogSnapshot catalog, ShopOddsInput state)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        if (catalog.Monsters.IsEmpty) return new([], [], 0, ["No catalog is available; import the wiki first."]);

        var eligible = EligibleByRarity(catalog, state);
        var weights = BuildWeights(state, notes);
        var chanceByRarity = Normalize(weights);
        var slots = CalculateBatomonSlots(state);
        var rarityOdds = ImmutableArray.CreateBuilder<RarityOdds>();
        var monsterOdds = ImmutableArray.CreateBuilder<MonsterOdds>();

        foreach (var rarity in Enum.GetValues<Rarity>())
        {
            var rolledAs = ResolveFallback(rarity, eligible);
            var probability = chanceByRarity.TryGetValue(rarity, out var chance) ? chance : 0m;
            rarityOdds.Add(new(rarity, probability, eligible[rarity].Length, rolledAs));
        }

        foreach (var monster in catalog.Monsters.Where(m => eligible[m.Rarity].Contains(m)).OrderBy(m => m.Rarity).ThenBy(m => m.Name))
        {
            var sourceRarities = chanceByRarity.Keys.Where(r => ResolveFallback(r, eligible) == monster.Rarity).ToArray();
            var perRoll = sourceRarities.Sum(r => chanceByRarity[r] / eligible[monster.Rarity].Length);
            var nextShop = 1m - Pow(1m - perRoll, slots);
            var explanation = sourceRarities.Contains(monster.Rarity)
                ? $"Eligible {monster.Rarity} from the {state.ActiveSet} set."
                : $"Receives fallback rolls from {string.Join(", ", sourceRarities)}.";
            monsterOdds.Add(new(monster.Id, monster.Name, monster.Rarity, perRoll, nextShop, explanation));
        }

        if (state.Effects.MysticIncenseQueued) notes.Add("Mystic Incense reserves one Mythical offer on the next fresh shop; its exact slot is game-state dependent.");
        if (state.Effects.RainbowPearl) notes.Add("Rainbow Pearl makes eligible future shop Batomon shiny.");
        return new(rarityOdds.ToImmutable(), monsterOdds.ToImmutable(), slots, notes.ToImmutable());
    }

    private static Dictionary<Rarity, ImmutableArray<MonsterDefinition>> EligibleByRarity(CatalogSnapshot catalog, ShopOddsInput state)
    {
        var owned = state.Team.Concat(state.Bench).Where(x => x.Level >= 3).Select(x => x.MonsterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in state.Team.Concat(state.Bench).Where(x => x.Level >= 3))
        {
            var baseSpecies = catalog.Monsters.FirstOrDefault(m => m.Id.Equals(unit.MonsterId, StringComparison.OrdinalIgnoreCase))?.BaseSpeciesId;
            if (!string.IsNullOrWhiteSpace(baseSpecies)) owned.Add(baseSpecies);
        }
        return Enum.GetValues<Rarity>().ToDictionary(
            rarity => rarity,
            rarity => catalog.Monsters.Where(m => m.Rarity == rarity && m.ActiveSets.Contains(state.ActiveSet) && !owned.Contains(m.Id)).ToImmutableArray());
    }

    private static Dictionary<Rarity, decimal> BuildWeights(ShopOddsInput state, ImmutableArray<string>.Builder notes)
    {
        if (state.Effects.ForceAllMythical || state.Effects.TicketRarity is not null)
        {
            var forced = state.Effects.ForceAllMythical ? Rarity.Mythical : state.Effects.TicketRarity!.Value;
            notes.Add($"The next shop is forced to {forced} Batomon offers.");
            return Enum.GetValues<Rarity>().ToDictionary(r => r, r => r == forced ? 1m : 0m);
        }
        var rank = Math.Min(14, Math.Max(1, state.ShopRank));
        var values = BaseWeights[rank];
        var weights = new Dictionary<Rarity, decimal>
        {
            [Rarity.Common] = values[0], [Rarity.Uncommon] = values[1], [Rarity.Rare] = values[2],
            [Rarity.SuperRare] = values[3], [Rarity.Legendary] = values[4], [Rarity.Mythical] = 0m
        };
        if (state.Effects.VipPass)
        {
            weights[Rarity.Common] = 0; weights[Rarity.Uncommon] = 0;
            if (weights[Rarity.Rare] + weights[Rarity.SuperRare] + weights[Rarity.Legendary] == 0) weights[Rarity.Rare] = 1;
        }
        if (state.Effects.ResearchNotes > 0) weights[Rarity.Legendary] += 20m * state.Effects.ResearchNotes;
        return weights;
    }

    private static Dictionary<Rarity, decimal> Normalize(IReadOnlyDictionary<Rarity, decimal> weights)
    {
        var total = weights.Values.Sum();
        return weights.ToDictionary(pair => pair.Key, pair => total == 0 ? 0m : pair.Value / total);
    }

    private static Rarity ResolveFallback(Rarity requested, IReadOnlyDictionary<Rarity, ImmutableArray<MonsterDefinition>> eligible)
    {
        for (var current = (int)requested; current >= 0; current--)
        {
            var rarity = (Rarity)current;
            if (!eligible[rarity].IsEmpty) return rarity;
        }
        return requested;
    }

    private static int CalculateBatomonSlots(ShopOddsInput state)
    {
        if (state.Effects.ForceAllMythical || state.Effects.TicketRarity is not null) return 5;
        return state.ItemUsesRemaining > 0 && state.HasEligibleItemOffer ? 4 : 5;
    }

    private static decimal Pow(decimal value, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= value;
        return result;
    }
}
