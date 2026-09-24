using System.Collections.Immutable;

namespace BatomonHelper.Core;

public sealed record RarityOdds(Rarity Rarity, decimal Probability, int EligibleCount, Rarity RolledAs);
public sealed record MonsterOdds(string MonsterId, string Name, Rarity Rarity, decimal PerBatomonRoll, decimal NextShopChance, string Explanation);
public sealed record ItemOdds(string ItemId, string Name, Rarity Rarity, decimal PerItemRoll, decimal NextShopChance, string Explanation);
public sealed record OddsResult(ImmutableArray<RarityOdds> RarityOdds, ImmutableArray<MonsterOdds> MonsterOdds, ImmutableArray<ItemOdds> ItemOdds, int BatomonSlotsNextShop, ImmutableArray<string> Notes);

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
        if (catalog.Monsters.IsEmpty) return new([], [], [], 0, ["No catalog is available; import the wiki first."]);
        var eligible = EligibleByRarity(catalog, snapshot);
        var visibleOdds = snapshot.RarityOdds.ToDictionary(odds => odds.Rarity, odds => odds.Probability);
        var slots = snapshot.ItemUsesRemaining > 0 ? 4 : 5;
        var rarityOdds = ImmutableArray.CreateBuilder<RarityOdds>();
        var monsterOdds = ImmutableArray.CreateBuilder<MonsterOdds>();
        var itemOdds = ImmutableArray.CreateBuilder<ItemOdds>();

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

        var itemsByRarity = catalog.Items.GroupBy(item => item.Rarity).ToDictionary(group => group.Key, group => group.ToImmutableArray());
        foreach (var item in catalog.Items.OrderBy(item => item.Rarity).ThenBy(item => item.Name))
        {
            var sameRarity = itemsByRarity[item.Rarity];
            var perRoll = snapshot.ItemUsesRemaining > 0 && sameRarity.Length > 0
                ? visibleOdds.GetValueOrDefault(item.Rarity) / sameRarity.Length
                : 0m;
            itemOdds.Add(new(item.Id, item.Name, item.Rarity, perRoll, perRoll,
                "Uses the captured {DisplayRarity(item.Rarity)} ribbon odds, split across {sameRarity.Length} item(s)."));
        }

        var notes = ImmutableArray.CreateBuilder<string>();
        notes.Add("Rarity odds are read directly from the shop ribbon.");
        if (snapshot.ItemUsesRemaining > 0) notes.Add($"{snapshot.ItemUsesRemaining}/{snapshot.DailyItemLimit} item uses: the fifth slot is an item, so four slots roll Batomon. Item odds use that one item slot and the same visible rarity ribbon.");
        else notes.Add("No item use remains: all five slots roll Batomon.");
        return new(rarityOdds.ToImmutable(), monsterOdds.ToImmutable(), itemOdds.ToImmutable(), slots, notes.ToImmutable());
    }

    private static Dictionary<Rarity, ImmutableArray<MonsterDefinition>> EligibleByRarity(CatalogSnapshot catalog, ShopOddsSnapshot snapshot)
    {
        var selectedRegion = snapshot.RegionFilter == MonsterRegionFilter.All ? null : $"{snapshot.RegionFilter} region";
        return Enum.GetValues<Rarity>().ToDictionary(
            rarity => rarity,
            rarity => catalog.Monsters.Where(monster => ShopCreatureEligibility.IsShopCreature(monster) && monster.Rarity == rarity && (selectedRegion is null || monster.Regions.Contains(selectedRegion))).ToImmutableArray());
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

    private static string DisplayRarity(Rarity rarity) => rarity == Rarity.SuperRare ? "Super Rare" : rarity.ToString();
}
