using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class MusicianAnalysisTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Self_knockout_keeps_the_single_electric_bonus_for_its_shock_cast(bool reference)
    {
        var (rules, board) = Setup();
        var result = new BoardAnalyzer(rules).Analyze(board, 100_000, 1, reference: reference);
        var shock = rules.Species.Single(s => s.Id == "galvanade").Normal.Single(l => l.Level == 2).Stats[(int)Stat.Shock];
        Assert.Equal(shock * 2.5, result.Checkpoints[^1].OutputTotals["Shock"], 8);
        Assert.False(result.Checkpoints[^1].Units.Single(u => u.Slot == 0).Alive);
    }

    [Fact]
    public void Second_electric_disables_bonus_even_after_one_knocks_itself_out()
    {
        var (rules, board) = Setup();
        board.Team[2] = new() { Instance = 3, SpeciesId = "galvanade", Level = 1 };
        var result = new BoardAnalyzer(rules).Analyze(board, 100_000, 1);
        var levels = rules.Species.Single(s => s.Id == "galvanade").Normal;
        var expected = levels.Single(l => l.Level == 2).Stats[(int)Stat.Shock]
            + levels.Single(l => l.Level == 1).Stats[(int)Stat.Shock];
        Assert.Equal(expected, result.Checkpoints[^1].OutputTotals["Shock"], 8);
    }

    [Fact]
    public void Advisor_does_not_bench_a_damage_dealer_for_a_second_electric_that_loses_musician_bonus()
    {
        var (rules, board) = Setup();
        Unit?[] bench = [new() { Instance = 3, SpeciesId = "galvanade", Level = 1 }, null, null, null];
        var analyzer = new BoardAnalyzer(rules);
        var current = analyzer.Analyze(board, 100_000, 1);
        var candidate = StateSnapshots.Copy(board);
        candidate.Team[2] = bench[0]!.Clone();
        Assert.True(analyzer.Analyze(candidate, 100_000, 1).Checkpoints[^1].Dps < current.Checkpoints[^1].Dps);
        var advice = new PositionAdvisor(rules).FindBetterPosition(board, bench, 100_000);
        Assert.Null(advice);
    }

    private static (RulesManifest, FrozenBoard) Setup()
    {
        var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json")))!;
        var stats = new double[9]; stats[(int)Stat.Damage] = 10; stats[(int)Stat.Multicast] = 1;
        var striker = RuleCompiler.Fixture().Species[0] with { Types = ["bug"], Normal = [new(1, 1, stats, [])] };
        rules = rules with { Species = [rules.Species.Single(s => s.Id == "galvanade"), striker] };
        return (rules, new(1, 100, "musician",
            [new() { Instance = 1, SpeciesId = "galvanade", Level = 2 },
             new() { Instance = 2, SpeciesId = striker.Id }, null, null, null, null], [], []));
    }
}
