using System.Collections.Immutable;

namespace BatomonHelper.Core;

public enum Rarity { Common, Uncommon, Rare, SuperRare, Legendary, Mythical }

public sealed record MonsterDefinition(
    string Id,
    string Name,
    Rarity Rarity,
    string? BaseSpeciesId,
    ImmutableHashSet<string> ActiveSets,
    bool HasShiny = true,
    string? SpritePath = null)
{
    public ImmutableHashSet<string> Regions { get; init; } = [];
    public string? ShinySpritePath { get; init; }
    public string? SpriteUrl { get; init; }
    public string? ShinySpriteUrl { get; init; }
    public ImmutableDictionary<string, string> ReferenceValues { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<string> ReferenceNotes { get; init; } = [];
    public MonsterRules? Rules { get; init; }
}

public sealed record TrainerDefinition(string Id, string Name, string? RuleText = null)
{
    public string? SpritePath { get; init; }
    public string? SpriteUrl { get; init; }
}

public sealed record ItemDefinition(string Id, string Name, string? RuleText = null, int? Price = null, string? SpritePath = null, Rarity Rarity = Rarity.Common);
public sealed record TrinketDefinition(string Id, string Name, Rarity Rarity, string? RuleText = null, bool IsUnique = false, string? SpritePath = null);

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
    public ImmutableArray<TrinketDefinition> Trinkets { get; init; } = [];
    public string? WikiVersion { get; init; }
    public string? ReferencePatchVersion { get; init; }

    public static CatalogSnapshot Empty { get; } = new(
        "empty", "unknown", null, DateTimeOffset.MinValue,
        [], string.Empty, ["No wiki snapshot has been imported."], []);
}

public sealed record ValidationResult(bool IsValid, ImmutableArray<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);
}
