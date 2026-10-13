using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatomonHelper.Core;

/// <summary>The immutable probability scenario used by both the pool browser.</summary>
public sealed record PoolContext(ShopOddsSnapshot Snapshot, ImmutableHashSet<string> LevelThreeMonsterIds,
    ImmutableHashSet<string> OwnedUniqueTrinketIds, OddsResult Odds, ImmutableArray<string> Assumptions)
{
    public string Revision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Snapshot = Snapshot with { CapturedAt = default },
        maxed = LevelThreeMonsterIds.Order(StringComparer.Ordinal), owned = OwnedUniqueTrinketIds.Order(StringComparer.Ordinal),
        Odds, Assumptions
    }))));

    public static PoolContext Create(CatalogSnapshot catalog, ShopOddsSnapshot displayed, BoardSnapshot? board,
        IEnumerable<string> markedMaxed, IEnumerable<string> ownedUnique, IOddsCalculator? calculator = null)
    {
        var maxed = markedMaxed.Concat(board?.Team.Concat(board.Bench).Where(s => s.Level >= 3)
            .Select(s => s.DefinitionId!).Where(id => id is not null) ?? [])
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        // Maxed evolution forms also remove their listed base species.
        foreach (var id in maxed.ToArray())
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = id;
            while (seen.Add(current) && catalog.Monsters.FirstOrDefault(m => m.Id == current)?.BaseSpeciesId is { } parent)
            { maxed = maxed.Add(parent); current = parent; }
        }
        var owned = ownedUnique.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var assumptions = ImmutableArray.CreateBuilder<string>();
        assumptions.Add($"Selected pool: {displayed.RegionFilter}. Region selection is supplied by you, not captured from the game.");
        if (board is not null && (displayed.ShopRank != board.ShopRank || !displayed.RarityOdds.SequenceEqual(board.RarityOdds)))
            assumptions.Add($"Manual odds scenario: rank {displayed.ShopRank}; captured rank {board.ShopRank}. Captured prices and gold still determine legal moves.");
        if (board is not null && displayed.ItemAvailable != (board.ItemUsesRemaining > 0))
            assumptions.Add($"Manual item-slot scenario: {(displayed.ItemAvailable ? "four" : "five")} Batomon rolls next shop.");
        assumptions.Add("Unmarked inventory and additional run eligibility are unknown; pool-dependent chances are estimates.");
        return new(displayed, maxed, owned, (calculator ?? new OddsCalculator()).Calculate(catalog, displayed, new(maxed, owned)), assumptions.ToImmutable());
    }
}

