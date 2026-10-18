using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class LiveBoardImportTests
{
    [Fact]
    public async Task Runs_training_analyzer_on_combat_metadata_exported_by_real_game_objects()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-dps.json"));
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var timestamp = JsonSerializer.Deserialize<GameStateMessage>(raw, GameStateBridge.JsonOptions)!.UpdatedAt;
        var message = GameStateBridge.Parse(raw, catalog, timestamp);
        var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json")))!;
        var imported = LiveBoardImport.Create(message.Board!, message.Combat!, rules);
        var result = new BoardAnalyzer(imported.Rules).Analyze(imported.Board, samples: 1);
        Assert.Equal(5, result.Checkpoints.Length);
        Assert.Equal(23, result.Checkpoints[0].Units[0].Stats[0]);
        Assert.Equal(5.5, result.Checkpoints[0].Units[0].Cooldown);
        Assert.True(result.Checkpoints[^1].Damage > 0);
        Assert.All(result.Curve, point => Assert.True(double.IsFinite(point.Dps)));
    }

    [Fact]
    public void Imports_permanent_stats_without_counting_ongoing_buff_twice_and_uses_game_cooldown()
    {
        var rules = RuleCompiler.Fixture();
        var basis = rules.Species[0];
        var stats = new double[9]; stats[0] = 10; stats[7] = 1;
        var species = basis with { Normal = [new(1, 1, stats,
            [new(Trigger.Ongoing, Operation.Add, Target.Self, Stat.Damage, 5)])] };
        rules = rules with { Species = [species] };
        var board = new BoardSnapshot { Day = 1, TeamHp = 300, TrainerId = rules.Trainers[0].Id, ShopRank = 1, Wins = 0,
            Team = [new(0, SlotOccupancy.Creature, species.Id, ShinyState.Normal, 1,
                [new(VisibleStatKind.Damage, 22)]), ..Enumerable.Range(1, 5).Select(index => new BoardSlot(index, SlotOccupancy.Empty))] };
        var permanent = (double[])stats.Clone(); permanent[0] = 17;
        var imported = LiveBoardImport.Create(board, new([new(0, permanent, 5, [])], []), rules);
        Assert.Equal(7, imported.Board.Team[0]!.Bonuses[0]);
        var result = new BoardAnalyzer(imported.Rules).Analyze(imported.Board, 100, 1);
        Assert.Equal(22, result.Checkpoints[0].Units[0].Stats[0]);
        Assert.Equal(5, result.Checkpoints[0].Units[0].Cooldown);
        Assert.Equal(10, species.Normal[0].Stats[0]);
        Assert.Equal(1, species.Normal[0].Cooldown);
        Assert.NotEqual(rules.Hash, imported.Rules.Hash);
    }

    [Fact]
    public void Rejects_missing_metadata_and_unknown_trinkets()
    {
        var rules = RuleCompiler.Fixture();
        var board = new BoardSnapshot { Day = 1, TeamHp = 300, TrainerId = rules.Trainers[0].Id,
            Team = [new(0, SlotOccupancy.Creature, rules.Species[0].Id, ShinyState.Normal, 1)] };
        Assert.Throws<InvalidDataException>(() => LiveBoardImport.Create(board, new([], []), rules));
        Assert.Throws<InvalidDataException>(() => LiveBoardImport.Create(board,
            new([new(0, new double[9], 1, [])], ["unknown-trinket"]), rules));
    }
}
