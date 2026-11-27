using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class PositionAdvisorTests
{
    [Fact]
    public void Healing_and_shielding_do_not_replace_a_faster_damage_board()
    {
        var rules = RuleCompiler.Fixture();
        var supportStats = new double[9];
        supportStats[(int)Stat.Heal] = 1000; supportStats[(int)Stat.Shield] = 1000;
        supportStats[(int)Stat.Multicast] = 1;
        rules = rules with { Species = [rules.Species[0], rules.Species[1] with
            { Normal = [new(1, 1, supportStats, [])] }] };
        var team = Enumerable.Range(1, 6).Select(i => (Unit?)new Unit { Instance = i, SpeciesId = "striker", Level = 1 }).ToArray();
        var board = new FrozenBoard(1, 300, "fixture", team, [], []);
        Assert.Null(new PositionAdvisor(rules).FindBetterPosition(board,
            [new() { Instance = 7, SpeciesId = "guard", Level = 1 }, null, null, null], targetHp: 300));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Random_trinket_targets_do_not_create_a_better_position(int strongSlot)
    {
        var (rules, board) = RandomTrinketBoard(strongSlot);
        var before = JsonSerializer.Serialize(new { rules, board });
        Assert.Null(new PositionAdvisor(rules).FindBetterPosition(board, new Unit?[4], targetHp: 100_000));
        Assert.Equal(before, JsonSerializer.Serialize(new { rules, board }));
        // Ordinary output projections still include the random trinket effect.
        var output = new BoardAnalyzer(rules).Analyze(board, targetHp: 100_000, samples: 1);
        Assert.Equal(.04, output.Checkpoints[0].Units.Sum(unit => unit.Stats[(int)Stat.Speed]), 8);
    }

    [Fact]
    public void Keeps_real_position_bonuses_when_a_trinket_also_has_a_random_effect()
    {
        var (rules, board) = RandomTrinketBoard(0);
        var trinket = rules.Trinkets[0];
        rules = rules with { Trinkets = [trinket with { Effects = [..trinket.Effects,
            new(Trigger.Ongoing, Operation.Add, Target.TopMiddle, Stat.Speed, .2)] }] };
        var advice = new PositionAdvisor(rules).FindBetterPosition(board, new Unit?[4], targetHp: 100_000);
        Assert.NotNull(advice);
        Assert.True(advice.ExcludesRandomTrinketEffects);
        Assert.Equal("striker", advice.Slots[1]!.SpeciesId);
        Assert.True(advice.Recommended.Checkpoints[^1].Dps > advice.Current.Checkpoints[^1].Dps);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public void Survival_chooses_effective_healing_and_cleansing_over_damage(int day)
    {
        var rules = RuleCompiler.Fixture();
        double[] healer = [0, 25000, 0, 0, 0, 0, 0, 1, 0];
        var support = rules.Species[1] with { Normal = [new(1, 1, healer,
            [new(Trigger.Cast, Operation.Cleanse, Target.All, Amount: 1, Percent: true)])] };
        rules = rules with
        {
            Build = "26773903",
            Species = [rules.Species[0], support],
            Parameters = rules.Parameters with { DailyHp = [.. rules.Parameters.DailyHp.Take(19), 453800] }
        };
        var team = Enumerable.Range(1, 6).Select(index => (Unit?)new Unit { Instance = index, SpeciesId = "striker" }).ToArray();
        var board = new FrozenBoard(day, 453800, "fixture", team, [], []);
        Unit?[] bench = [new() { Instance = 7, SpeciesId = "guard" }, null, null, null];
        var advisor = new PositionAdvisor(rules);
        Assert.Null(advisor.FindBetterPosition(board, bench, targetHp: board.MaxHp));
        var advice = Assert.IsType<PositionAdvice>(advisor.FindBetterPosition(board, bench,
            objective: PlacementObjective.MostHpAndShield));
        Assert.Equal(board.MaxHp, advice.Current.TargetHp);
        Assert.NotNull(advice.CurrentSurvival!.DefeatedAt);
        Assert.Null(advice.RecommendedSurvival!.DefeatedAt);
        Assert.Contains(advice.Slots.Take(6), unit => unit?.SpeciesId == "guard");
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(20, true)]
    public void Survival_fills_an_empty_slot_with_the_highest_dps_when_survival_is_equal(int hp, bool defeated)
    {
        var rules = RuleCompiler.Fixture();
        var team = new Unit?[6];
        for (var slot = 0; slot < 5; slot++)
            team[slot] = Striker(slot + 1, 100);
        var board = new FrozenBoard(1, hp, "fixture", team, [], []);
        Unit?[] bench = [Striker(6, 0), Striker(7, 50), null, null];

        var advice = Assert.IsType<PositionAdvice>(new PositionAdvisor(rules).FindBetterPosition(board, bench,
            targetHp: 100, objective: PlacementObjective.MostHpAndShield));

        Assert.Equal(advice.CurrentSurvival, advice.RecommendedSurvival);
        Assert.Equal(defeated, advice.RecommendedSurvival!.DefeatedAt.HasValue);
        Assert.All(advice.Slots.Take(6), slot => Assert.NotNull(slot));
        Assert.Equal(7, advice.Slots[5]!.Instance);
        Assert.True(advice.Recommended.Checkpoints[^1].Dps > advice.Current.Checkpoints[^1].Dps);

        static Unit Striker(int instance, double damageBonus)
        {
            var unit = new Unit { Instance = instance, SpeciesId = "striker" };
            unit.Bonuses[(int)Stat.Damage] = damageBonus;
            return unit;
        }
    }

    [Fact]
    public void Survival_prefers_an_occupied_slot_when_survival_and_dps_are_equal()
    {
        var rules = RuleCompiler.Fixture();
        var neutral = rules.Species[1] with { Normal = [new(1, null, new double[9], [])] };
        rules = rules with { Species = [neutral] };
        var board = new FrozenBoard(1, 1000, "fixture", new Unit?[6], [], []);
        Unit?[] bench = [new() { Instance = 1, SpeciesId = neutral.Id }, null, null, null];

        var advice = Assert.IsType<PositionAdvice>(new PositionAdvisor(rules).FindBetterPosition(board, bench,
            targetHp: 100, objective: PlacementObjective.MostHpAndShield));

        Assert.Equal(advice.CurrentSurvival, advice.RecommendedSurvival);
        Assert.Equal(advice.Current.Checkpoints[^1].Dps, advice.Recommended.Checkpoints[^1].Dps);
        Assert.Equal(1, Assert.Single(advice.Slots.Take(6).OfType<Unit>()).Instance);
    }

    [Fact]
    public void Mirrored_opponent_columns_and_top_middle_match_captured_positions()
    {
        var rules = RuleCompiler.Fixture();
        rules = rules with { Trinkets = new[] { "haste-crown", "quick-flag", "speed-crest" }.Select(id =>
        {
            var definition = InventoryRules.Definition(id)!.Value;
            return new InventoryDefinition(id, id, 0, false, 0, definition.Effects, "fixture", definition.Text);
        }).ToArray() };
        var board = new FrozenBoard(20, 453800, "fixture",
            Enumerable.Range(0, 6).Select(slot => (Unit?)new Unit { Instance = slot + 1, SpeciesId = "striker" }).ToArray(),
            [new("haste-crown", 1), new("quick-flag", 1), new("speed-crest", 3)], []);
        var analyzer = new BoardAnalyzer(rules);
        var own = analyzer.Analyze(board, samples: 1).Checkpoints[0].Units;
        var opponent = analyzer.Analyze(board with { Mirrored = true }, samples: 1).Checkpoints[0].Units;
        Assert.Equal([.04, .2, .45, .04, 0, .45], own.Select(unit => Math.Round(unit.Stats[(int)Stat.Speed], 2)));
        Assert.Equal([.45, .2, .04, .45, 0, .04], opponent.Select(unit => Math.Round(unit.Stats[(int)Stat.Speed], 2)));
    }

    [Fact]
    public void Mirrored_neighbor_targets_move_in_owner_direction_and_opposite_stays_screen_aligned()
    {
        var rules = RuleCompiler.Fixture();
        var actor = rules.Species[0];
        var species = actor with { Normal = [actor.Normal[0] with
        {
            Effects = [new(Trigger.BattleStart, Operation.Add, Target.Left, Stat.Speed, .2),
                new(Trigger.BattleStart, Operation.Knockout, Target.Opposite)]
        }] };
        rules = rules with { Species = [species, rules.Species[1]] };
        Unit Guard() => new() { SpeciesId = rules.Species[1].Id };
        var player = new FrozenBoard(1, 300, "fixture", [Guard(), Guard(), Guard(), null, null, null], [], []);
        var enemy = player with { Team = [Guard(), new() { SpeciesId = species.Id }, Guard(), null, null, null], Mirrored = true };
        var frames = new List<GameBattleFrame>();
        new Combat(rules).ResolveBattle(player, enemy, 69, audit: frames.Add);
        Assert.False(frames[0].Player.Units.Single(unit => unit.Slot == 1).Alive);
        Assert.Equal(.2, frames[0].Opponent.Units.Single(unit => unit.Slot == 2).Stats[(int)Stat.Speed]);
        Assert.Equal(0, frames[0].Opponent.Units.Single(unit => unit.Slot == 0).Stats[(int)Stat.Speed]);
    }

    private static (RulesManifest Rules, FrozenBoard Board) RandomTrinketBoard(int strongSlot)
    {
        var rules = RuleCompiler.Fixture();
        var strong = new double[9]; strong[(int)Stat.Damage] = 100; strong[(int)Stat.Multicast] = 1;
        var weak = new double[9]; weak[(int)Stat.Damage] = 1; weak[(int)Stat.Multicast] = 1;
        rules = rules with
        {
            Species = [rules.Species[0] with { Normal = [new(1, 1.1, strong, [])] },
                rules.Species[1] with { Normal = [new(1, 1.1, weak, [])] }],
            Trinkets = [new("tempo-charm", "Tempo Charm", 0, false, 0,
                InventoryRules.Definition("tempo-charm")!.Value.Effects, "fixture", "Random speed buff")]
        };
        var units = Enumerable.Range(0, 6).Select(slot => (Unit?)new Unit
        { Instance = slot + 1, SpeciesId = slot == strongSlot ? "striker" : "guard", Level = 1 }).ToArray();
        return (rules, new(1, 100, "fixture", units, [new("tempo-charm", 1)], []));
    }

    [Fact]
    public void Superseded_live_search_returns_no_partial_advice_without_throwing()
    {
        var rules = RuleCompiler.Fixture();
        var current = new FrozenBoard(1, 100, "fixture", [new() { SpeciesId = "striker" }, null, null, null, null, null], [], []);
        var advisor = new PositionAdvisor(rules);
        var token = new CancellationToken(true);
        Assert.Null(advisor.TryFindBetterPosition(current, new Unit?[4], cancellation: token));
        var error = Assert.Throws<OperationCanceledException>(() => advisor.FindBetterPosition(current, new Unit?[4], cancellation: token));
        Assert.Equal(token, error.CancellationToken);
    }

    [Fact]
    public async Task Screenshot_board_keeps_Pylongs_left_ally_bonus_and_rejects_the_reported_swap()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-pylong.json"));
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var timestamp = JsonSerializer.Deserialize<GameStateMessage>(raw, GameStateBridge.JsonOptions)!.UpdatedAt;
        var message = GameStateBridge.Parse(raw, catalog, timestamp);
        var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "combat-rules.json")))!;
        var live = LiveBoardImport.Create(message.Board!, message.Combat!, rules);
        var units = live.Board.Team.Concat(live.Bench!).OfType<Unit>().ToArray();
        Assert.Equal(units.Length, units.Select(unit => unit.Instance).Distinct().Count());
        var analyzer = new BoardAnalyzer(live.Rules);
        var current = analyzer.Analyze(live.Board, targetHp: 18250, samples: 1);
        Assert.Equal(150, current.Checkpoints[0].Units.Single(unit => unit.Slot == 0).Stats[(int)Stat.Shock]);
        Assert.Equal(0, current.Checkpoints[0].Units.Single(unit => unit.Slot == 4).Stats[(int)Stat.Shock]);
        var swapped = StateSnapshots.Copy(live.Board);
        (swapped.Team[0], swapped.Team[4]) = (swapped.Team[4], swapped.Team[0]);
        var worse = analyzer.Analyze(swapped, targetHp: 18250, samples: 1);
        Assert.Equal(75, worse.Checkpoints[0].Units.Single(unit => unit.Slot == 4).Stats[(int)Stat.Shock]);
        Assert.True(worse.MedianKillSeconds > current.MedianKillSeconds);
        var advice = new PositionAdvisor(live.Rules).FindBetterPosition(live.Board, live.Bench!, targetHp: 18250);
        Assert.False(advice?.Slots[0]?.SpeciesId == "clawnetic" && advice?.Slots[4]?.SpeciesId == "galvanade");
    }

    [Theory]
    [InlineData("Ally behind has +100% Shock.", Target.Left)]
    [InlineData("Give the ally in front +1 Multicast and increase this monster's Cooldown by 8 seconds for this battle.", Target.Right)]
    [InlineData("Give the Fire ally above +2 Burn permanently.", Target.Above)]
    public void Distinguishes_horizontal_and_vertical_ability_targets(string text, Target target)
    {
        var effects = RuleCompiler.CompileText(text, Trigger.Ongoing, out var understood);
        Assert.True(understood);
        Assert.Equal(target, effects[0].Target);
    }

    [Fact]
    public void Horizontal_targets_stop_at_row_edges()
    {
        Assert.Empty(Combat.Targets(0, Target.Left));
        Assert.Empty(Combat.Targets(3, Target.Left));
        Assert.Empty(Combat.Targets(2, Target.Right));
        Assert.Empty(Combat.Targets(5, Target.Right));
        Assert.Equal(new[] { 0 }, Combat.Targets(1, Target.Left));
        Assert.Equal(new[] { 5 }, Combat.Targets(4, Target.Right));
        Assert.Equal(new[] { 1 }, Combat.Targets(4, Target.Above));
    }
}
