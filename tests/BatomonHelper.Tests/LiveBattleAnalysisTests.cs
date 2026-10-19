using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class LiveBattleAnalysisTests
{
    [Fact]
    public async Task Reads_real_game_battle_start_export_and_runs_the_matchup()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-battle.json"));
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var timestamp = JsonSerializer.Deserialize<GameStateMessage>(raw, GameStateBridge.JsonOptions)!.UpdatedAt;
        var message = GameStateBridge.Parse(raw, catalog, timestamp);
        Assert.Null(message.Board);
        Assert.Equal("scorchimp", message.Battle!.Player.Team[0].DefinitionId);
        Assert.Equal("dribblet", message.Battle.Opponent.Team[5].DefinitionId);
        var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json")))!;
        var result = LiveBattleAnalysis.Analyze(message.Battle, rules, samples: 1);
        Assert.Null(message.Battle.Outcome);
        Assert.True(result.Player.Checkpoints[^1].Damage > 0);
        Assert.True(result.Opponent.Checkpoints[0].OutputPerSecond["Heal"] > 0);
        foreach (var invalid in new[] {
            message.Battle with { Opponent = message.Battle.Opponent with { Day = 99 } },
            message.Battle with { Opponent = message.Battle.Opponent with { TeamHp = 0 } },
            message.Battle with { Opponent = message.Battle.Opponent with { Team = [] } } })
            Assert.Throws<InvalidDataException>(() => GameStateBridge.Parse(
                JsonSerializer.Serialize(message with { Battle = invalid }, GameStateBridge.JsonOptions), catalog, timestamp));
    }

    private static GameBattleSide Side(double damage, int hp = 100)
    {
        var stats = new double[9]; stats[0] = damage; stats[7] = 1;
        return new(1, hp, "fixture", Enumerable.Range(0, 6).Select(index => index == 0
            ? new BoardSlot(index, SlotOccupancy.Creature, "striker", ShinyState.Normal, 1, [])
            : new BoardSlot(index, SlotOccupancy.Empty, Stats: [])).ToImmutableArray(), new([new(0, stats, 1, [])], []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preserves_authoritative_game_outcome_without_inventing_a_probability(bool playerWon)
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-battle.json"));
        var message = JsonSerializer.Deserialize<GameStateMessage>(raw, GameStateBridge.JsonOptions)!;
        message = message with { Battle = message.Battle! with { Outcome = new(playerWon) } };
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var parsed = GameStateBridge.Parse(JsonSerializer.Serialize(message, GameStateBridge.JsonOptions), catalog, message.UpdatedAt);
        Assert.Equal(playerWon, parsed.Battle!.Outcome!.PlayerWon);
        Assert.Equal(playerWon ? "WIN" : "LOSS", BatomonHelper.Overlay.BattleOutcomePresentation.Summary(parsed.Battle.Outcome));
        Assert.Equal("Calculating fight result...", BatomonHelper.Overlay.BattleOutcomePresentation.Summary(null));
    }

    [Fact]
    public void Projects_both_teams_without_changing_the_exported_result_or_inputs()
    {
        var battle = new GameBattleState(Side(50), Side(5), new(false));
        var before = JsonSerializer.Serialize(battle);
        var output = LiveBattleAnalysis.Analyze(battle, RuleCompiler.Fixture());
        Assert.True(output.Player.Checkpoints[^1].Damage > output.Opponent.Checkpoints[^1].Damage);
        Assert.Equal(100, output.Player.TargetHp);
        Assert.False(battle.Outcome!.PlayerWon);
        Assert.Equal(before, JsonSerializer.Serialize(battle));
    }

    [Fact]
    public void Rejects_bad_metadata_and_honors_cancellation()
    {
        var player = Side(50);
        Assert.Throws<InvalidDataException>(() => LiveBattleAnalysis.Analyze(
            new(player, player with { Combat = new([], []) }), RuleCompiler.Fixture(), samples: 1));
        Assert.Throws<OperationCanceledException>(() => LiveBattleAnalysis.Analyze(
            new(player, player), RuleCompiler.Fixture(), cancellation: new CancellationToken(true)));
    }
}
