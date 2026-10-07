using BatomonHelper.Core;
using System.Collections.Immutable;

// Synthetic accepted states with catalog baseline badges, not screen-recognition fixtures.
// Criteria are a human evaluation rubric, not a requirement to choose one exact strategy.
internal sealed record StrategyScenario(string Name, BoardSnapshot Board, string[] Criteria);
internal static class StrategyScenarios
{
    public static StrategyScenario[] Create(CatalogSnapshot catalog)
    {
        var baseline = new BoardSnapshot { TrainerId = "musician", Day = 4, Lives = 5, Wins = 3, WinTarget = 10,
            TeamHp = 500, Gold = 60, ShopRank = 4, RarityOdds = ShopRankOdds.ForRank(4), FreeRerolls = 1, ShopLocked = false,
            Team = Slots([], 6), Bench = Slots([], 4), Shop = Slots([], 5), CapturedAt = DateTimeOffset.UtcNow };
        return [
            new("shock-non-electric", baseline with { Team = Slots(["galvanade", "scorchimp", "mosslug"], 6), Shop = Offers("velocect", "pebbler") },
                ["Recognizes that non-Electric direct attackers benefit from enemy Shock.", "Explains Velocect's repeated direct hits without requiring Electric typing."]),
            new("multicast-self-knockout", baseline with { Team = Slots(["galvanade", "velocect", "mosslug"], 6), Shop = Offers("black-feather", "humbolt"),
                ItemUsesCounterVisible = true, ItemUsesRemaining = 1, ItemUsesMaximum = 1 },
                ["Queued multicast repeats stop on self-knockout.", "Black Feather has a random target; does not promise a selected recipient."]),
            new("trigger-positioning", baseline with { Team = Slots(["velocect", "pebbler", "mosslug", "scorchimp", "cicadence"], 6), Shop = Offers("gildshell") },
                ["Cicadence requires a Bug ally above.", "Triggering retains cooldown progress and uses the target's current multicast."]),
            new("adjacency-and-charge", baseline with { Team = Slots(["dracana", "mosslug", "pebbler"], 6), Bench = Slots(["onsetra"], 4), Shop = Offers("gildshell") },
                ["Deploys or positions the charge recipient behind Dracana using the 3x2 board.", "Bench creatures do not receive active combat charge."]),
            new("shiny-variant", baseline with { Team = Slots(["galvanade", "mosslug"], 6), Shop = Offers("velocect").SetItem(0, Unit(0, "velocect", shiny: true) with { Price = 15, Affordable = true }) },
                ["Uses the shiny level rules for the visible shiny offer.", "Does not substitute normal multicast or invent an evolution."]),
            new("unavailable-build-partner", baseline with { Team = Slots(["galvanade", "velocect", "cherubble"], 6), Shop = Offers("pebbler", "gildshell") },
                ["Checks missing build partners against the selected regional pool.", "Never invents a positive shop chance for a species absent from that pool."]),
            new("low-lives", baseline with { Lives = 1, Team = Slots(["pebbler", "mosslug"], 6), Shop = Offers("velocect", "humbolt", "gildshell") },
                ["Favors feasible immediate battle strength over a distant late-game core.", "Avoids saving or speculative rerolls while useful affordable deployments are visible."]),
            new("maxed-species", baseline with { Team = Slots(["galvanade", "velocect", "mosslug"], 6).SetItem(1, Unit(1, "velocect", 3)), Shop = Offers("humbolt", "gildshell") },
                ["Does not chase Velocect through future normal shop rolls when maxed.", "Keeps captured offers distinct from future pool estimates."]),
            new("burn-scaling", baseline with { TrainerId = "black-belt", Team = Slots(["scorchimp", "blixie", "mosslug"], 6), Shop = Offers("lignite", "magmite") },
                ["Explains a printed Burn interaction rather than inventing synergy from shared typing.", "Treats reference patch fields and permanent/battle-only scaling according to supplied evidence."])
        ];

        ImmutableArray<BoardSlot> Slots(string[] ids, int count) => Enumerable.Range(0, count)
            .Select(i => i < ids.Length ? Unit(i, ids[i]) : new BoardSlot(i, SlotOccupancy.Empty, Stats: [])).ToImmutableArray();
        BoardSlot Unit(int index, string id, int level = 1, bool shiny = false)
        {
            var monster = catalog.Monsters.Single(m => m.Id == id); var rules = monster.Rules!;
            var entry = (shiny ? rules.ShinyLevels : rules.Levels).Single(l => l.Level == level);
            var stats = entry.Stats.Where(s => s.Value is not null && s.Kind != VisibleStatKind.Unknown).ToImmutableArray();
            if (entry.Multicast is > 1 && !stats.Any(s => s.Kind == VisibleStatKind.Multicast)) stats = stats.Add(new(VisibleStatKind.Multicast, entry.Multicast));
            return new(index, SlotOccupancy.Creature, id, shiny ? ShinyState.Shiny : ShinyState.Normal, level, stats);
        }
        ImmutableArray<BoardSlot> Offers(params string[] ids) => Enumerable.Range(0, 5).Select(i =>
        {
            if (i >= ids.Length) return new BoardSlot(i, SlotOccupancy.Empty, Stats: []);
            if (catalog.Items.FirstOrDefault(item => item.Id == ids[i]) is { } item)
                return new BoardSlot(i, SlotOccupancy.Item, item.Id, Stats: [], Price: item.Price ?? 10, Affordable: true);
            var cost = catalog.Monsters.Single(m => m.Id == ids[i]).ReferenceValues.GetValueOrDefault("Cost");
            var price = int.TryParse(cost, out var parsed) ? parsed : 15;
            return Unit(i, ids[i]) with { Price = price, Affordable = price <= baseline.Gold };
        }).ToImmutableArray();
    }
}
