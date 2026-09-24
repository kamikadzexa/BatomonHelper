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

public sealed record ValidationResult(bool IsValid, ImmutableArray<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);
}
