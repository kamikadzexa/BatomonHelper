using System.Collections.Immutable;

namespace BatomonHelper.Core;

public enum Rarity { Common, Uncommon, Rare, SuperRare, Legendary, Mythical }
public enum ShopSlotKind { Batomon, Item, Empty }

public sealed record MonsterDefinition(
    string Id,
    string Name,
    Rarity Rarity,
    string? BaseSpeciesId,
    ImmutableHashSet<string> ActiveSets,
    bool HasShiny = true);

public sealed record CatalogSnapshot(
    string SnapshotId,
    string DeclaredSteamBuild,
    string? Balance,
    DateTimeOffset RetrievedAt,
    ImmutableArray<string> SourceUrls,
    string ContentHash,
    ImmutableArray<string> Warnings,
    ImmutableArray<MonsterDefinition> Monsters)
{
    public static CatalogSnapshot Empty { get; } = new(
        "empty", "unknown", null, DateTimeOffset.MinValue,
        [], string.Empty, ["No wiki snapshot has been imported."], []);
}

public sealed record UnitState(
    string MonsterId,
    int Level,
    bool IsShiny,
    int Position,
    ImmutableArray<string> Equipment,
    ImmutableDictionary<string, int> Stats);

public sealed record ShopSlotState(
    ShopSlotKind Kind,
    string? OfferId,
    Rarity? Rarity,
    int Price,
    bool IsShiny,
    string? Note = null);

public sealed record RunEffects(
    int ResearchNotes = 0,
    bool VipPass = false,
    bool MysticIncenseQueued = false,
    bool ForceAllMythical = false,
    Rarity? TicketRarity = null,
    bool RainbowPearl = false,
    int ShinyChanceMultiplier = 1);

public sealed record RunState(
    int Day,
    int ShopRank,
    int Gold,
    int Wins,
    int Lives,
    string ActiveSet,
    string? TrainerId,
    int ItemUsesRemaining,
    bool HasEligibleItemOffer,
    bool IsShopLocked,
    int FreeRerolls,
    bool IsOverflow,
    ImmutableArray<UnitState> Team,
    ImmutableArray<UnitState> Bench,
    ImmutableArray<ShopSlotState> Shop,
    ImmutableArray<string> Trinkets,
    ImmutableArray<string> Items,
    RunEffects Effects)
{
    public static RunState Empty { get; } = new(
        1, 1, 0, 0, 0, "starter", null, 0, false, false, 0, false,
        [], [], [], [], [], new());
}

public sealed record ValidationResult(bool IsValid, ImmutableArray<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);
}

public static class RunStateValidator
{
    public static ValidationResult Validate(RunState state, CatalogSnapshot catalog)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (state.Day is < 1 or > 999) errors.Add("Day must be between 1 and 999.");
        if (state.ShopRank is < 1 or > 99) errors.Add("Shop rank must be between 1 and 99.");
        if (state.Gold is < 0 or > 9_999) errors.Add("Gold is outside a plausible range.");
        if (state.Team.Length > 6 || state.Bench.Length > 4) errors.Add("Team/bench capacity is invalid.");
        if (state.Shop.Length > 5) errors.Add("A normal shop cannot contain more than five slots.");
        if (state.IsOverflow && state.Team.Length + state.Bench.Length < 10) errors.Add("Overflow requires a full team and bench.");

        var ids = catalog.Monsters.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in state.Team.Concat(state.Bench))
        {
            if (unit.Level is < 1 or > 4) errors.Add($"{unit.MonsterId} has an invalid level.");
            if (catalog.Monsters.Length > 0 && !ids.Contains(unit.MonsterId)) errors.Add($"Unknown monster id: {unit.MonsterId}.");
        }
        return errors.Count == 0 ? ValidationResult.Success : new(false, errors.ToImmutable());
    }
}
