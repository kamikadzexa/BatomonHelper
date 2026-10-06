using System.Collections.Immutable;

namespace BatomonHelper.Core;

public enum SlotOccupancy { Empty, Creature, Item, Unknown }
public enum ShinyState { Normal, Shiny, Unknown }
public enum VisibleStatKind { Damage, Heal, Shield, Burn, Poison, Shock, CooldownSpeed, Multicast, Unknown }
public sealed record VisibleStat(VisibleStatKind Kind, int? Value);
public sealed record BoardSlot(int Index, SlotOccupancy Occupancy, string? DefinitionId = null,
    ShinyState Variant = ShinyState.Unknown, int? Level = null,
    ImmutableArray<VisibleStat> Stats = default, int? Price = null, bool? Affordable = null);

/// <summary>Only the values visible in a single shopping-screen frame. Null means unread, never zero.</summary>
public sealed record BoardSnapshot
{
    public string? TrainerId { get; init; }
    public int? Day { get; init; }
    public int? Lives { get; init; }
    public int? Wins { get; init; }
    public int? WinTarget { get; init; }
    public int? TeamHp { get; init; }
    public int? Gold { get; init; }
    public int? ShopRank { get; init; }
    public ImmutableArray<CapturedRarityOdds> RarityOdds { get; init; } = [];
    public int? RerollCost { get; init; }
    public int? FreeRerolls { get; init; }
    public int? ItemUsesRemaining { get; init; }
    public int? ItemUsesMaximum { get; init; }
    public bool ItemUsesCounterVisible { get; init; }
    public bool? ShopLocked { get; init; }
    public ImmutableArray<BoardSlot> Team { get; init; } = [];
    public ImmutableArray<BoardSlot> Bench { get; init; } = [];
    public ImmutableArray<BoardSlot> Shop { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; }
    public MonsterRegionFilter RegionFilter { get; init; }

    public ShopOddsSnapshot ToShopOddsSnapshot(CatalogSnapshot catalog)
    {
        var validation = BoardSnapshotValidator.Validate(this, catalog);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors));
        var item = Shop.FirstOrDefault(slot => slot.Occupancy == SlotOccupancy.Item);
        var definition = catalog.Items.FirstOrDefault(entry => entry.Id == item?.DefinitionId);
        return new(catalog.Trainers.First(entry => entry.Id == TrainerId).Name, ShopRank!.Value,
            Gold!.Value, RarityOdds, ItemUsesRemaining > 0,
            definition is null ? null : new(definition.Name, item!.Price!.Value, definition.RuleText), CapturedAt)
        { RegionFilter = RegionFilter };
    }
}

public static class BoardSnapshotValidator
{
    public static ValidationResult Validate(BoardSnapshot board, CatalogSnapshot catalog)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (!catalog.Trainers.Any(trainer => trainer.Id == board.TrainerId)) errors.Add("Choose a catalog trainer.");
        Check(board.Day, 1, 999, "Day"); Check(board.Lives, 0, 99, "Lives");
        Check(board.Wins, 0, 999, "Wins"); Check(board.WinTarget, 1, 999, "Win target");
        if (board.Wins > board.WinTarget) errors.Add("Wins exceed the win target.");
        Check(board.TeamHp, 0, 9_999_999, "Team HP"); Check(board.Gold, 0, 9_999, "Gold");
        Check(board.ShopRank, ShopRankOdds.MinimumRank, ShopRankOdds.MaximumRank, "Shop rank");
        if (board.RarityOdds.Length != 5 || board.RarityOdds.Select(odds => odds.Rarity).Distinct().Count() != 5 ||
            board.RarityOdds.Any(odds => !Enum.IsDefined(odds.Rarity) || odds.Rarity == Rarity.Mythical || odds.Probability is < 0 or > 1) ||
            board.RarityOdds.Sum(odds => odds.Probability) != 1) errors.Add("Read all five displayed odds; they must total 100%.");
        if ((board.RerollCost is null) == (board.FreeRerolls is null)) errors.Add("Read either reroll cost or free rerolls.");
        if (board.RerollCost is < 0 || board.FreeRerolls is < 0) errors.Add("Rerolls cannot be negative.");
        if (board.ItemUsesCounterVisible)
        { Check(board.ItemUsesRemaining, 0, 999, "Item uses remaining"); Check(board.ItemUsesMaximum, 0, 999, "Item uses maximum"); }
        else if (board.ItemUsesRemaining is not null || board.ItemUsesMaximum is not null) errors.Add("An absent item counter has no visible numeric values.");
        if (board.Shop.Any(slot => slot.Occupancy == SlotOccupancy.Item) && !board.ItemUsesCounterVisible) errors.Add("Read the item-use counter above the item offer.");
        if (board.ItemUsesRemaining > board.ItemUsesMaximum) errors.Add("Item uses remaining exceed the maximum.");
        if (board.ShopLocked is null) errors.Add("Read the shop lock state.");
        Slots(board.Team, 6, "Team", false); Slots(board.Bench, 4, "Bench", false); Slots(board.Shop, 5, "Shop", true);
        return errors.Count == 0 ? ValidationResult.Success : new(false, errors.ToImmutable());

        void Check(int? value, int min, int max, string name)
        { if (value is null || value < min || value > max) errors.Add($"{name} must be between {min} and {max}."); }
        void Slots(ImmutableArray<BoardSlot> slots, int count, string area, bool shop)
        {
            if (slots.Length != count || !slots.Select(slot => slot.Index).SequenceEqual(Enumerable.Range(0, count)))
                errors.Add($"{area} must preserve all {count} positions.");
            foreach (var slot in slots)
            {
                var label = $"{area} {slot.Index + 1}";
                if (!Enum.IsDefined(slot.Occupancy)) errors.Add($"Invalid occupant type for {label}.");
                if (slot.Occupancy == SlotOccupancy.Unknown) errors.Add($"Identify {label} or mark it empty.");
                if (slot.Occupancy == SlotOccupancy.Empty) continue;
                if (slot.Occupancy == SlotOccupancy.Creature)
                {
                    if (!catalog.Monsters.Any(monster => monster.Id == slot.DefinitionId)) errors.Add($"Choose a creature for {label}.");
                    if (!Enum.IsDefined(slot.Variant) || slot.Variant == ShinyState.Unknown) errors.Add($"Choose normal or shiny for {label}.");
                    if (!shop) Check(slot.Level, 1, 4, $"{label} level");
                    if (slot.Stats.IsDefault || slot.Stats.Any(stat => !Enum.IsDefined(stat.Kind) || stat.Kind == VisibleStatKind.Unknown || stat.Value is null or < 0 or > 9_999_999)) errors.Add($"Read the visible badges for {label}.");
                    if (!slot.Stats.IsDefault && slot.Stats.Select(stat => stat.Kind).Distinct().Count() != slot.Stats.Length) errors.Add($"Duplicate stat badges in {label}.");
                }
                if (slot.Occupancy == SlotOccupancy.Item && (!shop || !catalog.Items.Any(item => item.Id == slot.DefinitionId))) errors.Add($"Choose a shop item for {label}.");
                if (shop) Check(slot.Price, 0, 9_999, $"{label} price");
                if (shop && slot.Affordable != (slot.Price <= board.Gold)) errors.Add($"Affordability does not agree with gold for {label}.");
            }
        }
    }
}
