using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class PositionAdvisorTests
{
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
