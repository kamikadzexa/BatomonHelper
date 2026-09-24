using System.Collections.Immutable;

namespace BatomonHelper.Core;

public enum Rarity { Common, Uncommon, Rare, SuperRare, Legendary, Mythical }

public sealed record MonsterDefinition(
    string Id,
    string Name,
    Rarity Rarity,
    string? BaseSpeciesId,
    ImmutableHashSet<string> ActiveSets,
    bool HasShiny = true)
{
    public ImmutableHashSet<string> Regions { get; init; } = [];
}

public sealed record TrainerDefinition(string Id, string Name, string? RuleText = null);

public sealed record ItemDefinition(string Id, string Name, string? RuleText = null);

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
    public ImmutableArray<TrainerDefinition> Trainers { get; init; } = [];
    public ImmutableArray<ItemDefinition> Items { get; init; } = [];

    public static CatalogSnapshot Empty { get; } = new(
        "empty", "unknown", null, DateTimeOffset.MinValue,
        [], string.Empty, ["No wiki snapshot has been imported."], []);
}

public sealed record ValidationResult(bool IsValid, ImmutableArray<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);
}
