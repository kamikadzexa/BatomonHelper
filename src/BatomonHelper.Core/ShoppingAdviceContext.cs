using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatomonHelper.Core;

/// <summary>The immutable probability scenario used by both the browser and advice.</summary>
public sealed record ShoppingAdviceContext(ShopOddsSnapshot Snapshot, ImmutableHashSet<string> LevelThreeMonsterIds,
    ImmutableHashSet<string> OwnedUniqueTrinketIds, OddsResult Odds, ImmutableArray<string> Assumptions)
{
    public string Revision => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Snapshot = Snapshot with { CapturedAt = default },
        maxed = LevelThreeMonsterIds.Order(StringComparer.Ordinal), owned = OwnedUniqueTrinketIds.Order(StringComparer.Ordinal),
        Odds, Assumptions
    }))));

    public static ShoppingAdviceContext Create(CatalogSnapshot catalog, ShopOddsSnapshot displayed, BoardSnapshot? board,
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

/// <summary>Projects only action facts whose outcomes are known. It is not a combat or purchase-effect simulator.</summary>
public static class ShoppingSequenceValidator
{
    public static bool CanFollow(ShoppingObservation observation, CatalogSnapshot catalog, GameAction first, GameAction second)
    {
        if (first.Id == second.Id || Project(observation, catalog, first) is not { } after) return false;
        return new LegalActionGenerator().Generate(after, catalog).Any(a => a.SameMove(second));
    }

    public static ShoppingObservation? Project(ShoppingObservation observation, CatalogSnapshot catalog, GameAction action)
    {
        if (observation.Board is not { } board || !new LegalActionGenerator().Generate(observation, catalog).Any(a => a.SameMove(action))) return null;
        switch (action.Kind)
        {
            case ActionKind.SetLock:
                return observation with { Board = board with { ShopLocked = action.Locked } };
            case ActionKind.Move:
            case ActionKind.Swap:
                var source = action.Source!; var target = action.Target!;
                var sourceSlot = Slot(source); var targetSlot = Slot(target);
                Set(source, targetSlot with { Index = source.Index }); Set(target, sourceSlot with { Index = target.Index });
                // Details follow their creatures, never remain attached to old positions.
                return observation with { Board = board, Details = observation.Details.Select(d => d.Slot == source ? d with { Slot = target }
                    : d.Slot == target ? d with { Slot = source } : d).ToImmutableArray() };
            case ActionKind.BuyCreature:
                // Hidden trinkets, on-buy effects, auto-merges and unverified rules end a concrete sequence.
                if (!observation.InventoryResolved || !SafePurchase()) return null;
                var offer = board.Shop[action.Source!.Index];
                var group = Group(offer.DefinitionId);
                if (board.Team.Concat(board.Bench).Any(s => s.Occupancy == SlotOccupancy.Creature && Group(s.DefinitionId) == group)) return null;
                var purchased = offer with { Index = action.Target!.Index, Price = null, Affordable = null,
                    Level = offer.Level ?? observation.OfferLevels.FirstOrDefault(l => l.Index == offer.Index && l.Resolved)?.Level };
                Set(action.Target, purchased);
                board = board with { Gold = board.Gold - action.Cost,
                    Shop = board.Shop.SetItem(offer.Index, new(offer.Index, SlotOccupancy.Empty, Stats: [])) };
                board = board with { Shop = board.Shop.Select(s => s.Occupancy == SlotOccupancy.Empty ? s : s with { Affordable = s.Price <= board.Gold }).ToImmutableArray() };
                return observation with { Board = board };
            default: return null;
        }

        BoardSlot Slot(SlotAddress address) => address.Area == SlotArea.Team ? board.Team[address.Index] : board.Bench[address.Index];
        void Set(SlotAddress address, BoardSlot slot) => board = address.Area == SlotArea.Team
            ? board with { Team = board.Team.SetItem(address.Index, slot) } : board with { Bench = board.Bench.SetItem(address.Index, slot) };
        string? Group(string? id) => catalog.Monsters.FirstOrDefault(m => m.Id == id)?.Rules?.MergeGroup ?? id;
        bool SafePurchase()
        {
            var trainer = catalog.Trainers.FirstOrDefault(t => t.Id == board.TrainerId);
            if (trainer?.RuleText is null || PurchaseEffect(trainer.RuleText)) return false;
            foreach (var id in board.Team.Concat(board.Bench).Append(board.Shop[action.Source!.Index])
                .Where(s => s.Occupancy == SlotOccupancy.Creature).Select(s => s.DefinitionId))
            {
                var rules = catalog.Monsters.FirstOrDefault(m => m.Id == id)?.Rules;
                if (rules is null || !rules.UnverifiedPatchFields.IsEmpty || PurchaseEffect(rules.AbilityTrigger) || PurchaseEffect(rules.ShinyAbilityTrigger) ||
                    !rules.Abilities.IsEmpty && rules.AbilityTrigger is null || !rules.ShinyAbilities.IsEmpty && rules.ShinyAbilityTrigger is null ||
                    rules.Abilities.Values.Concat(rules.ShinyAbilities.Values).Any(PurchaseEffect)) return false;
            }
            var knownTrinkets = observation.Inventory.Where(i => i.Resolved && i.IsTrinket && i.Quantity > 0).Select(i => i.DefinitionId)
                .Concat(observation.AdviceContext?.OwnedUniqueTrinketIds ?? []);
            return knownTrinkets.All(id => catalog.Trinkets.FirstOrDefault(t => t.Id == id)?.RuleText is { } text && !PurchaseEffect(text));
        }
    }
    private static bool PurchaseEffect(string? text) => text is not null && new[] { "buy", "bought", "purchase", "shop", "merge", "discount", "cost" }
        .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}
