using System.Collections.Immutable;

namespace BatomonHelper.Core;

/// <summary>
/// The values visible on the Batomon shop screen. These remain deliberately close to
/// the UI so that screen recognition never has to invent legacy memory-reader fields.
/// </summary>
public sealed record ShopHud(
    int Day,
    int Hearts,
    int ProgressCurrent,
    int ProgressTarget,
    int HitPoints,
    int Rank,
    int RankProgress,
    int RankProgressTarget,
    int RerollCost,
    int UpgradeCost,
    bool IsLocked);

public sealed record ShopState(
    ShopHud Hud,
    string? TrainerId,
    string ActiveSet,
    ImmutableArray<UnitState> Team,
    ImmutableArray<UnitState> Bench,
    ImmutableArray<ShopSlotState> Shop,
    ImmutableArray<string> Inventory,
    int ItemUsesRemaining,
    RunEffects Effects,
    DateTimeOffset CapturedAt)
{
    public ShopOddsInput ToOddsInput() => new(
        Hud.Day,
        Hud.Rank,
        ActiveSet,
        Team,
        Bench,
        Shop,
        Effects,
        ItemUsesRemaining,
        Shop.Any(slot => slot.Kind == ShopSlotKind.Item));
}

/// <summary>Minimal, explicit input used by the next-shop odds calculation.</summary>
public sealed record ShopOddsInput(
    int Day,
    int ShopRank,
    string ActiveSet,
    ImmutableArray<UnitState> Team,
    ImmutableArray<UnitState> Bench,
    ImmutableArray<ShopSlotState> Shop,
    RunEffects Effects,
    int ItemUsesRemaining,
    bool HasEligibleItemOffer);
