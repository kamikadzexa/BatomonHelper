using System.Collections.Immutable;

namespace BatomonHelper.Core;

public sealed record RarityOdds(Rarity Rarity, decimal Probability, int EligibleCount, Rarity RolledAs);
public sealed record MonsterOdds(string MonsterId, string Name, Rarity Rarity, decimal PerBatomonRoll, decimal NextShopChance, string Explanation);
public sealed record OddsResult(ImmutableArray<RarityOdds> RarityOdds, ImmutableArray<MonsterOdds> MonsterOdds, int BatomonSlotsNextShop, ImmutableArray<string> Notes);

public interface IOddsCalculator
{
    OddsResult Calculate(CatalogSnapshot catalog, ShopOddsSnapshot snapshot);
}

/// <summary>Projects the shop's visible final odds onto its eligible monster catalog.</summary>
public sealed class OddsCalculator : IOddsCalculator
{
    private static readonly Rarity[] RibbonOrder = [Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.SuperRare, Rarity.Legendary];

    public OddsResult Calculate(CatalogSnapshot catalog, ShopOddsSnapshot snapshot)
    {
        if (catalog.Monsters.IsEmpty) return new([], [], 0, ["No catalog is available; import the wiki first."]);
        var eligible = EligibleByRarity(catalog, snapshot);
        var visibleOdds = snapshot.RarityOdds.ToDictionary(odds => odds.Rarity, odds => odds.Probability);
        var slots = snapshot.ItemUsesRemaining > 0 ? 4 : 5;
        var rarityOdds = ImmutableArray.CreateBuilder<RarityOdds>();
        var monsterOdds = ImmutableArray.CreateBuilder<MonsterOdds>();

        foreach (var rarity in RibbonOrder)
        {
            var rolledAs = ResolveFallback(rarity, eligible);
            rarityOdds.Add(new(rarity, visibleOdds.GetValueOrDefault(rarity), eligible[rarity].Length, rolledAs));
        }

        foreach (var monster in catalog.Monsters.Where(monster => eligible[monster.Rarity].Contains(monster)).OrderBy(monster => monster.Rarity).ThenBy(monster => monster.Name))
        {
            var sourceRarities = RibbonOrder.Where(rarity => ResolveFallback(rarity, eligible) == monster.Rarity).ToArray();
            var perRoll = sourceRarities.Sum(rarity => visibleOdds.GetValueOrDefault(rarity) / eligible[monster.Rarity].Length);
            var nextShop = 1m - Pow(1m - perRoll, slots);
            monsterOdds.Add(new(monster.Id, monster.Name, monster.Rarity, perRoll, nextShop,
                $"Uses the captured {string.Join(", ", sourceRarities)} ribbon odds."));
        }

        var notes = ImmutableArray.CreateBuilder<string>();
        notes.Add("Rarity odds are read directly from the shop ribbon.");
        if (snapshot.ItemUsesRemaining > 0) notes.Add($"{snapshot.ItemUsesRemaining}/{snapshot.DailyItemLimit} item uses: the fifth slot is an item, so four slots roll Batomon.");
        else notes.Add("No item use remains: all five slots roll Batomon.");
        return new(rarityOdds.ToImmutable(), monsterOdds.ToImmutable(), slots, notes.ToImmutable());
    }

    private static Dictionary<Rarity, ImmutableArray<MonsterDefinition>> EligibleByRarity(CatalogSnapshot catalog, ShopOddsSnapshot snapshot)
    {
        var selectedRegion = snapshot.RegionFilter == MonsterRegionFilter.All ? null : $"{snapshot.RegionFilter} region";
        return Enum.GetValues<Rarity>().ToDictionary(
            rarity => rarity,
            rarity => catalog.Monsters.Where(monster => monster.Rarity == rarity && monster.ActiveSets.Contains(snapshot.ActiveSet) && (selectedRegion is null || monster.Regions.Contains(selectedRegion))).ToImmutableArray());
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

    private static decimal Pow(decimal value, int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++) result *= value;
        return result;
    }
}
