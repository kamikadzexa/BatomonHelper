using System.Collections.Immutable;
using System.Text.Json;
using BatomonHelper.Core;
using BatomonHelper.Overlay;
using BatomonHelper.Simulation;

namespace BatomonHelper.Tests;

public sealed class RecordedRunRulesTests
{
    [Theory]
    [InlineData("recorded-chemist-day1.json", true)]
    [InlineData("recorded-masked-day4.json", false)]
    public void Recorded_fights_match_game_outcomes_in_both_resolvers(string fixture, bool expectedWin)
    {
        var fight = JsonSerializer.Deserialize<CapturedFight>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), GameStateBridge.JsonOptions)!;
        var rules = Rules();
        var player = Import(fight.Start.Player, rules);
        var opponent = Import(fight.Start.Opponent, player.Rules);
        foreach (var reference in new[] { false, true })
        {
            var result = new Combat(opponent.Rules).ResolveBattle(StateSnapshots.Copy(player.Board), StateSnapshots.Copy(opponent.Board), 69, reference: reference);
            Assert.Equal(fight.Outcome!.PlayerWon, result.PlayerWon);
            Assert.Equal(expectedWin, result.PlayerWon);
        }
        if (!expectedWin)
        {
            // The recorded Alpinine has "all" types. Rainbow Berry grants 13 * 4 damage.
            var probe = new CombatProbe { Duration = 1 };
            new Combat(opponent.Rules).ResolveBattle(opponent.Board, player.Board, 69, probe: probe);
            var pebbler = probe.Frames[0].Units.Single(unit => unit.Species == "pebbler");
            Assert.Equal(52, pebbler.Stats[(int)Stat.Damage]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_import_preserves_chemist_progress_and_masked_man_choices(bool reference)
    {
        var rules = Rules();
        var side = new GameBattleSide(6, 1000, "masked-man",
            [new(0, SlotOccupancy.Creature, "venopuff", ShinyState.Normal, 1)],
            new([new(0, [0, 0, 0, 0, 4, 0, 0, 1, 0], 3.5, [])], [],
                Counters: new() { ["chemist-levels"] = 7 }, ExtraTrainers: ["chemist"]));
        var imported = Import(side, rules);
        var result = new BoardAnalyzer(imported.Rules).Analyze(imported.Board, 1000, 1, reference: reference);
        Assert.Equal(14, result.Checkpoints[0].Units.Single().Stats[(int)Stat.Poison]);
        Assert.Equal(["chemist"], imported.Board.ExtraTrainers);
        Assert.Equal(7, imported.Board.Counters!["chemist-levels"]);
        side.Combat.Counters!["chemist-levels"] = 20;
        Assert.Equal(7, imported.Board.Counters["chemist-levels"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Combat_rounds_output_stats_like_the_game(bool reference)
    {
        var rules = Rules();
        var unit = new Unit { SpeciesId = "rhizuka", Level = 1 };
        var stats = LevelRules.Resolve(rules.Species.Single(s => s.Id == unit.SpeciesId), 1, false).Stats;
        unit.Bonuses[(int)Stat.Damage] = 205 - stats[(int)Stat.Damage];
        var board = new FrozenBoard(10, 10000, "witch", [unit, null, null, null, null, null], [new("giant-club", 1)], []);
        // Use a known fractional ongoing multiplier independent of the species cooldown.
        rules = rules with { Trinkets = [new("rounding", "Rounding", 0, false, 0,
            [new(Trigger.Ongoing, Operation.Multiply, Target.All, Stat.Damage, .5)], "fixture", "")] };
        board = board with { Bag = [new("rounding", 1)] };
        var result = new BoardAnalyzer(rules).Analyze(board, 10000, 1, reference: reference);
        Assert.Equal(308, result.Checkpoints[0].Units.Single().Stats[(int)Stat.Damage]);
    }

    private static RulesManifest Rules() => JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "combat-rules.json")))!;

    private static LiveBoardProjection Import(GameBattleSide side, RulesManifest rules) => LiveBoardImport.Create(
        new BoardSnapshot { Day = side.Day, TeamHp = side.TeamHp, TrainerId = side.TrainerId, ShopRank = side.Rank, Wins = side.Wins,
            Team = side.Team.Select(slot => slot with { DefinitionId = slot.DefinitionId is null ? null : GameCatalogIds.Resolve(slot.DefinitionId, rules.Species.Select(s => s.Id)) }).ToImmutableArray() },
        side.Combat with { Bench = null }, rules);
}
