using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class PositionAdvisorTests
{
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
        var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json")))!;
        var live = LiveBoardImport.Create(message.Board!, message.Combat!, rules);
        var units = live.Board.Team.Concat(live.Bench!).OfType<Unit>().ToArray();
        Assert.Equal(units.Length, units.Select(unit => unit.Instance).Distinct().Count());
        var analyzer = new BoardAnalyzer(live.Rules);
        var current = analyzer.Analyze(live.Board, samples: 1);
        Assert.Equal(150, current.Checkpoints[0].Units.Single(unit => unit.Slot == 0).Stats[(int)Stat.Shock]);
        Assert.Equal(0, current.Checkpoints[0].Units.Single(unit => unit.Slot == 4).Stats[(int)Stat.Shock]);
        var swapped = StateSnapshots.Copy(live.Board);
        (swapped.Team[0], swapped.Team[4]) = (swapped.Team[4], swapped.Team[0]);
        var worse = analyzer.Analyze(swapped, samples: 1);
        Assert.Equal(75, worse.Checkpoints[0].Units.Single(unit => unit.Slot == 4).Stats[(int)Stat.Shock]);
        Assert.True(worse.MedianKillSeconds > current.MedianKillSeconds);
        var advice = new PositionAdvisor(live.Rules).FindBetterPosition(live.Board, live.Bench!);
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
