using System.Collections.Immutable;

namespace BatomonHelper.Core;

/// <summary>One manually requested read of the visible shop-odds UI.</summary>
public sealed record ShopOddsSnapshot(
    string? Trainer,
    ImmutableArray<UnitState> Team,
    int ShopRank,
    int Gold,
    ImmutableArray<CapturedRarityOdds> RarityOdds,
    int ItemUsesRemaining,
    int DailyItemLimit,
    ShopItemOffer? ItemOffer,
    DateTimeOffset CapturedAt,
    string ActiveSet = "starter");

public sealed record CapturedRarityOdds(Rarity Rarity, decimal Probability);
public sealed record ShopItemOffer(string Name, int Price, string? RuleText = null);

public static class ShopOddsSnapshotValidator
{
    public static ValidationResult Validate(ShopOddsSnapshot snapshot, CatalogSnapshot catalog)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (snapshot.ShopRank is < 1 or > 99) errors.Add("Shop rank must be between 1 and 99.");
        if (snapshot.Gold is < 0 or > 9_999) errors.Add("Gold must be non-negative.");
        if (snapshot.ItemUsesRemaining is < 0 or > 99 || snapshot.DailyItemLimit is < 0 or > 99 || snapshot.ItemUsesRemaining > snapshot.DailyItemLimit) errors.Add("Item-use values are invalid.");
        if (snapshot.ItemUsesRemaining > 0 && snapshot.ItemOffer is null) errors.Add("The current fifth-slot item offer must be identified while an item use remains.");
        if (snapshot.Team.Length > 6) errors.Add("The active team cannot contain more than six Batomon.");
        if (snapshot.RarityOdds.Length != 5 || snapshot.RarityOdds.Select(odds => odds.Rarity).Distinct().Count() != 5) errors.Add("All five visible rarity odds are required.");
        if (snapshot.RarityOdds.Any(odds => odds.Probability is < 0m or > 1m)) errors.Add("Rarity odds must be between 0% and 100%.");
        if (snapshot.RarityOdds.Sum(odds => odds.Probability) != 1m) errors.Add("Visible rarity odds must total 100%.");
        var ids = catalog.Monsters.Select(monster => monster.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in snapshot.Team)
        {
            if (unit.Level is < 1 or > 4) errors.Add($"{unit.MonsterId} has an invalid level.");
            if (catalog.Monsters.Length > 0 && !ids.Contains(unit.MonsterId)) errors.Add($"Unknown monster id: {unit.MonsterId}.");
        }
        return errors.Count == 0 ? ValidationResult.Success : new(false, errors.ToImmutable());
    }
}
