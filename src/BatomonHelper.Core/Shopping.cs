using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatomonHelper.Core;

public sealed record MonsterLevelRules(int Level, decimal? Cooldown, int? Multicast, ImmutableArray<VisibleStat> Stats);
public sealed record MonsterRules(ImmutableArray<string> Types, ImmutableArray<string> Keywords,
    ImmutableArray<MonsterLevelRules> Levels, ImmutableArray<MonsterLevelRules> ShinyLevels,
    ImmutableDictionary<int, string> Abilities, ImmutableDictionary<int, string> ShinyAbilities,
    string? Evolution, string MergeGroup, string SourceUrl, DateTimeOffset RetrievedAt)
{
    public ImmutableArray<string> UnverifiedPatchFields { get; init; } = [];
}

public enum ShoppingPanel { Unknown, Shop, Backpack, TargetSelection, TrinketReward, Event, Battle, Result, RunEnd }
public enum ActionKind { BuyCreature, BuyItem, Sell, Move, Swap, Merge, UseItem, ChooseReward, ChooseEvent, Reroll, SetLock, Finish }
public enum SlotArea { Team, Bench, Shop, Inventory, Choice }
public sealed record SlotAddress(SlotArea Area, int Index);
public sealed record ObservedInventoryEntry(int Index, string DefinitionId, int? Quantity, bool IsTrinket, bool Resolved);
public sealed record CreatureDetails(SlotAddress Slot, ImmutableArray<string> Types, int? MergeProgress,
    int? SellPrice, decimal? Cooldown, bool Resolved);
public sealed record ObservedChoice(int Index, string Label, string? DefinitionId, int? Cost,
    bool? Enabled, string? Effect, bool Resolved);
public sealed record ObservedTarget(SlotAddress Slot, double X, double Y, double Width, double Height, bool Resolved);
public sealed record RecognitionFact(string Name, double Confidence, bool Resolved, string? Note = null);
public sealed record ObservedOfferLevel(int Index, int Level, bool Resolved, string Basis);
public sealed record GameGeometry(long WindowHandle, int ProcessId, int ClientX, int ClientY,
    int ClientWidth, int ClientHeight, int ContentX, int ContentY, int ContentWidth, int ContentHeight, uint Dpi);
public sealed record ShoppingObservation
{
    public BoardSnapshot? Board { get; init; }
    public ShoppingPanel Panel { get; init; }
    public ImmutableArray<ObservedInventoryEntry> Inventory { get; init; } = [];
    public bool InventoryResolved { get; init; }
    public ImmutableArray<CreatureDetails> Details { get; init; } = [];
    public ImmutableArray<ObservedChoice> Choices { get; init; } = [];
    public ImmutableArray<ObservedTarget> InteractionTargets { get; init; } = [];
    public ImmutableArray<RecognitionFact> Recognition { get; init; } = [];
    public ImmutableArray<ObservedOfferLevel> OfferLevels { get; init; } = [];
    public ImmutableArray<SlotAddress> EnabledTargets { get; init; } = [];
    public bool TargetsResolved { get; init; }
    public string? PendingDefinitionId { get; init; }
    public string? PanelText { get; init; }
    public string? Outcome { get; init; }
    public GameGeometry? Geometry { get; init; }
    public bool Resolved { get; init; }
    public bool Foreground { get; init; }
    public MonsterRegionFilter? RunRegion { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Revision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { Board = Board is null ? null : Board with { CapturedAt = default, RegionFilter = MonsterRegionFilter.All },
        Panel, Inventory, InventoryResolved, Details, Choices, InteractionTargets, OfferLevels, EnabledTargets, TargetsResolved, PendingDefinitionId, PanelText, Geometry, RunRegion }))));
}

public sealed record GameAction(string Id, ActionKind Kind, string Label, SlotAddress? Source = null,
    SlotAddress? Target = null, int Cost = 0, bool? Locked = null)
{
    public string ExpectedResult { get; init; } = "";
    public ImmutableArray<string> Prerequisites { get; init; } = [];
    public bool SameMove(GameAction other) => Id == other.Id && Kind == other.Kind && Source == other.Source && Target == other.Target &&
        Cost == other.Cost && Locked == other.Locked && Label == other.Label && ExpectedResult == other.ExpectedResult && Prerequisites.SequenceEqual(other.Prerequisites);
}
public sealed record ShoppingDecision(string Revision, GameAction Action, string Reason, string Objective,
    ImmutableArray<string> References, double InferenceMilliseconds = 0)
{
    public string NextStep { get; init; } = "";
}
public sealed record VerifiedAction(GameAction Action, string BeforeRevision, string AfterRevision, DateTimeOffset At);
public sealed record PlannerRequest(ShoppingObservation Observation, CatalogSnapshot Catalog, StrategyKnowledge Knowledge,
    ImmutableArray<GameAction> Candidates, OddsResult? Odds, bool OddsEstimated, string? Objective,
    ImmutableArray<VerifiedAction> RecentActions);
public interface ILegalActionGenerator
{
    ImmutableArray<GameAction> Generate(ShoppingObservation observation, CatalogSnapshot catalog);
}
public interface IShoppingPlanner
{
    Task<ShoppingDecision> ProposeAsync(PlannerRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Legality is independent of model output. Missing facts never become assumed zeroes.</summary>
public sealed class LegalActionGenerator : ILegalActionGenerator
{
    public ImmutableArray<GameAction> Generate(ShoppingObservation o, CatalogSnapshot catalog)
    {
        var result = ImmutableArray.CreateBuilder<GameAction>();
        if (!o.Resolved || o.Board is not { } b || !BoardSnapshotValidator.Validate(b, catalog).IsValid) return [];
        if (o.Panel is ShoppingPanel.TrinketReward or ShoppingPanel.Event)
        {
            foreach (var c in o.Choices.Where(c => c.Resolved && c.Enabled == true && c.Cost is >= 0 && c.Cost <= b.Gold && !string.IsNullOrWhiteSpace(c.Effect) &&
                o.InteractionTargets.Any(t => t.Slot == new SlotAddress(SlotArea.Choice,c.Index) && t.Resolved)))
                Add(o.Panel == ShoppingPanel.TrinketReward ? ActionKind.ChooseReward : ActionKind.ChooseEvent,
                    $"Choose {c.Label}", new(SlotArea.Choice, c.Index), cost: c.Cost!.Value, expected: "Choice panel closes or advances; selected effect is observed.");
            return result.ToImmutable();
        }
        if (o.Panel == ShoppingPanel.TargetSelection)
        {
            if (o.TargetsResolved && o.PendingDefinitionId is not null && catalog.Items.Any(i => i.Id == o.PendingDefinitionId))
                foreach (var target in o.EnabledTargets)
                    if (target.Area is SlotArea.Team or SlotArea.Bench && Slot(target)?.Occupancy == SlotOccupancy.Creature && o.InteractionTargets.Any(t => t.Slot == target && t.Resolved))
                    Add(ActionKind.UseItem, $"Apply {o.PendingDefinitionId} to {Describe(target)}", target: target,
                        expected: "Target panel closes and the target or inventory changes.");
            return result.ToImmutable();
        }
        if (o.Panel == ShoppingPanel.Backpack)
        {
            InventoryActions();
            return result.ToImmutable();
        }
        if (o.Panel != ShoppingPanel.Shop) return [];
        var slots = b.Team.Select(s => (Address: new SlotAddress(SlotArea.Team, s.Index), Slot: s))
            .Concat(b.Bench.Select(s => (Address: new SlotAddress(SlotArea.Bench, s.Index), Slot: s))).ToArray();
        foreach (var offer in b.Shop.Where(s => s.Price is >= 0 && s.Affordable == true && s.Price <= b.Gold))
        {
            if (offer.Occupancy == SlotOccupancy.Creature && (offer.Level ?? o.OfferLevels.FirstOrDefault(l => l.Index == offer.Index && l.Resolved)?.Level) is >= 1 and <= 4)
                foreach (var empty in slots.Where(s => s.Slot.Occupancy == SlotOccupancy.Empty))
                    Add(ActionKind.BuyCreature, $"Buy {Name(offer.DefinitionId)} into {Describe(empty.Address)}",
                        new(SlotArea.Shop, offer.Index), empty.Address, offer.Price!.Value,
                        expected: "Offer disappears, gold changes by the captured price, and the purchased creature occupies the target.");
            // Item purchases may open another panel; a follow-up requires newly observed facts.
            if (offer.Occupancy == SlotOccupancy.Item && b.ItemUsesRemaining > 0 &&
                catalog.Items.Any(i => i.Id == offer.DefinitionId && !string.IsNullOrWhiteSpace(i.RuleText)))
                Add(ActionKind.BuyItem, $"Buy {Name(offer.DefinitionId)}", new(SlotArea.Shop, offer.Index), cost: offer.Price!.Value,
                    expected: "Gold changes by the price and the offer changes, or a recognized item target/choice panel opens.");
        }
        foreach (var source in slots.Where(s => s.Slot.Occupancy == SlotOccupancy.Creature))
        {
            var detail = o.Details.FirstOrDefault(d => d.Slot == source.Address && d.Resolved);
            if (detail?.SellPrice is >= 0)
                Add(ActionKind.Sell, $"Sell {Name(source.Slot.DefinitionId)} from {Describe(source.Address)} for ${detail.SellPrice}",
                    source.Address, cost: -detail.SellPrice.Value, expected: "Source is empty and the observed sale amount is added to gold.");
            foreach (var destination in slots.Where(s => s.Address != source.Address))
            {
                if (destination.Slot.Occupancy == SlotOccupancy.Empty)
                    Add(ActionKind.Move, $"Move {Name(source.Slot.DefinitionId)} to {Describe(destination.Address)}", source.Address, destination.Address,
                        expected: "The source creature occupies the target and its source becomes empty.");
                else if (destination.Slot.Occupancy == SlotOccupancy.Creature)
                {
                    // A drag onto a matching unit may merge rather than swap. Never call it a swap.
                    if (MergeGroup(source.Slot) != MergeGroup(destination.Slot))
                    {
                        if (string.CompareOrdinal(Describe(source.Address), Describe(destination.Address)) < 0)
                            Add(ActionKind.Swap, $"Swap {Describe(source.Address)} and {Describe(destination.Address)}", source.Address, destination.Address,
                                expected: "The two distinct creatures exchange slots without changing gold.");
                    }
                    else if (source.Slot.Level == destination.Slot.Level && source.Slot.Level is 1 or 2 &&
                        detail?.MergeProgress is >= 1 && detail.MergeProgress < (source.Slot.Level == 1 ? 3 : 2) &&
                        o.Details.Any(d => d.Slot == destination.Address && d.Resolved && d.MergeProgress is >= 1 && d.MergeProgress < (source.Slot.Level == 1 ? 3 : 2)))
                        Add(ActionKind.Merge, $"Merge {Name(source.Slot.DefinitionId)} from {Describe(source.Address)} into {Describe(destination.Address)}",
                            source.Address, destination.Address, expected: "Source is consumed and target merge progress/level changes, possibly opening a reward.");
                }
            }
        }
        // Backpack controls must be visibly observed, never inferred from a previously opened panel.
        if (b.FreeRerolls > 0 || b.RerollCost is >= 0 && b.RerollCost <= b.Gold)
            Add(ActionKind.Reroll, b.FreeRerolls > 0 ? "Reroll shop (free)" : $"Reroll shop (${b.RerollCost})",
                cost: b.FreeRerolls > 0 ? 0 : b.RerollCost!.Value, expected: "Gold or free-reroll count decreases; a new shop is observed.");
        Add(ActionKind.SetLock, b.ShopLocked == true ? "Unlock shop" : "Lock shop", locked: b.ShopLocked != true,
            expected: "Captured lock state toggles.");
        Add(ActionKind.Finish, "Finish shopping and start the battle manually", expected: "The current team enters battle when you start it.");
        return result.ToImmutable();

        string? MergeGroup(BoardSlot s) => catalog.Monsters.FirstOrDefault(m => m.Id == s.DefinitionId)?.Rules?.MergeGroup ?? s.DefinitionId;
        BoardSlot? Slot(SlotAddress s) => s.Area == SlotArea.Team ? b.Team.ElementAtOrDefault(s.Index) : s.Area == SlotArea.Bench ? b.Bench.ElementAtOrDefault(s.Index) : null;
        void InventoryActions()
        {
            if (!o.InventoryResolved) return;
            foreach (var item in o.Inventory.Where(i => i.Resolved && !i.IsTrinket && i.Quantity > 0 &&
                o.InteractionTargets.Any(t => t.Slot == new SlotAddress(SlotArea.Inventory, i.Index) && t.Resolved) &&
                catalog.Items.Any(d => d.Id == i.DefinitionId && !string.IsNullOrWhiteSpace(d.RuleText))))
                Add(ActionKind.UseItem, $"Use {Name(item.DefinitionId)} from backpack", new(SlotArea.Inventory, item.Index),
                    expected: "Item quantity/effect changes or a recognized target panel opens.");
        }
        string Name(string? id) => catalog.Monsters.FirstOrDefault(m => m.Id == id)?.Name ?? catalog.Items.FirstOrDefault(i => i.Id == id)?.Name ?? id ?? "Unknown";
        void Add(ActionKind kind, string label, SlotAddress? source = null, SlotAddress? target = null, int cost = 0, bool? locked = null, string expected = "")
        {
            var id = $"{kind}:{source?.Area}:{source?.Index}:{target?.Area}:{target?.Index}:{locked}";
            result.Add(new(id, kind, label, source, target, cost, locked) { ExpectedResult = expected,
                Prerequisites = ["Accepted shopping capture", "Required action facts are known"] });
        }
    }
    private static string Describe(SlotAddress address) => $"{address.Area} {address.Index + 1}";
}
