using System.Collections.Immutable;

namespace BatomonHelper.Core;

public enum MonsterRegionFilter { All, Jinto, Pantra }

/// <summary>One manually requested read of the visible shop-odds UI.</summary>
public sealed record ShopOddsSnapshot(
    string? Trainer,
    int ShopRank,
    int Gold,
    ImmutableArray<CapturedRarityOdds> RarityOdds,
    bool ItemAvailable,
    ShopItemOffer? ItemOffer,
    DateTimeOffset CapturedAt,
    string ActiveSet = "starter")
{
    public MonsterRegionFilter RegionFilter { get; init; } = MonsterRegionFilter.All;
}

public sealed record CapturedRarityOdds(Rarity Rarity, decimal Probability);
public sealed record ShopItemOffer(string Name, int Price, string? RuleText = null);

public static class ShopOddsSnapshotValidator
{
    public static ValidationResult Validate(ShopOddsSnapshot snapshot, CatalogSnapshot catalog)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (snapshot.ShopRank is < ShopRankOdds.MinimumRank or > ShopRankOdds.MaximumRank) errors.Add($"Shop rank must be between {ShopRankOdds.MinimumRank} and {ShopRankOdds.MaximumRank}.");
        if (snapshot.Gold is < 0 or > 9_999) errors.Add("Gold must be non-negative.");
        if (snapshot.RarityOdds.Length != 5 || snapshot.RarityOdds.Select(odds => odds.Rarity).Distinct().Count() != 5) errors.Add("All five visible rarity odds are required.");
        if (snapshot.RarityOdds.Any(odds => odds.Probability is < 0m or > 1m)) errors.Add("Rarity odds must be between 0% and 100%.");
        if (snapshot.RarityOdds.Sum(odds => odds.Probability) != 1m) errors.Add("Visible rarity odds must total 100%.");
        return errors.Count == 0 ? ValidationResult.Success : new(false, errors.ToImmutable());
    }
}
