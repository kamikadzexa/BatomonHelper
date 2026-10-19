using System.Collections.Immutable;

namespace BatomonHelper.Core;

/// <summary>The standard shop ribbon for each supported shop rank.</summary>
public static class ShopRankOdds
{
    public const int MinimumRank = 1;
    /// <summary>The maximum shop rank; the game treats rank 14 and above as this rank.</summary>
    public const int MaximumRank = 14;

    private static readonly IReadOnlyDictionary<int, int[]> Percentages = new Dictionary<int, int[]>
    {
        [1] = [100, 0, 0, 0, 0], [2] = [90, 10, 0, 0, 0], [3] = [75, 25, 0, 0, 0], [4] = [67, 30, 3, 0, 0],
        [5] = [55, 30, 15, 0, 0], [6] = [45, 32, 20, 3, 0], [7] = [30, 40, 25, 5, 0], [8] = [25, 30, 35, 10, 0],
        [9] = [20, 25, 38, 15, 2], [10] = [15, 20, 40, 20, 5], [11] = [10, 20, 35, 25, 10], [12] = [5, 15, 30, 35, 15],
        [13] = [1, 10, 30, 39, 20], [14] = [1, 2, 20, 47, 30]
    };

    public static ImmutableArray<CapturedRarityOdds> ForRank(int rank)
    {
        var values = Percentages[Math.Clamp(rank, MinimumRank, MaximumRank)];
        return Enum.GetValues<Rarity>().Take(5).Select((rarity, index) => new CapturedRarityOdds(rarity, values[index] / 100m)).ToImmutableArray();
    }

    /// <summary>Collapses game ranks above the maximum into the maximum-rank state.</summary>
    public static int Normalize(int rank) => Math.Min(rank, MaximumRank);

    public static bool TryFindRank(IEnumerable<int?> values, out int rank)
    {
        var candidate = values.ToArray();
        var matches = Percentages.Where(entry => candidate.Length == 5 &&
            candidate.SequenceEqual(entry.Value.Select(value => (int?)value)))
            .Select(entry => entry.Key).ToArray();
        rank = matches.Length == 1 ? matches[0] : 0;
        return rank != 0;
    }

    public static ImmutableArray<CapturedRarityOdds> ApplyCapturedModifier(int capturedRank, IEnumerable<CapturedRarityOdds> capturedOdds, int targetRank)
    {
        var baseline = ForRank(capturedRank).ToDictionary(odds => odds.Rarity, odds => odds.Probability);
        var target = ForRank(targetRank).ToDictionary(odds => odds.Rarity, odds => odds.Probability);
        return target.Select(entry => new CapturedRarityOdds(entry.Key, Math.Clamp(entry.Value + (capturedOdds.FirstOrDefault(odds => odds.Rarity == entry.Key)?.Probability ?? baseline[entry.Key]) - baseline[entry.Key], 0m, 1m))).ToImmutableArray();
    }
}
