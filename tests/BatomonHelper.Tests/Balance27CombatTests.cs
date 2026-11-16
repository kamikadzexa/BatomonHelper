using BatomonHelper.Simulation;
using System.Text.Json;
using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class Balance27CombatTests
{
    private static RulesManifest Rules() => JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "combat-rules.json")))!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_trainer_and_balance_values_work_in_live_projections(bool reference)
    {
        var rules = Rules();
        Assert.Equal("26572818", rules.Build); Assert.Equal("27", rules.Balance);
        Assert.Empty(rules.Issues); Assert.True(rules.CombatReady);
        Assert.Equal(148, rules.Species.Length); Assert.Equal(24, rules.Trainers.Length); Assert.Equal(95, rules.Trinkets.Length);
        Assert.Equal(5, rules.Species.Single(s => s.Id == "lignite").Normal[0].Cooldown);
        Assert.Equal(30, rules.Species.Single(s => s.Id == "aviarab").Cost);
        Assert.Equal(15, rules.Species.Single(s => s.Id == "sarudo").Cost);
        var lure=Assert.Single(rules.Trinkets,t=>t.Id=="plain-lure").Effects;
        Assert.Equal(20,Assert.Single(lure).Amount);Assert.Equal(ShopTrait.CommonWeight,lure[0].ShopTrait);
        var sweet=rules.Trinkets.Single(t=>t.Id=="sweet-lure").Effects;
        Assert.Equal([10d,15d],sweet.Select(effect=>effect.Amount));
        var board = new FrozenBoard(1, 100, "witch",
            [new() { Instance = 1, SpeciesId = "furnadon", Level = 1 }, null, null, null, null, null],
            [new("junk", 4), new("bargain-bin", 2)], []);
        var result = new BoardAnalyzer(rules).Analyze(board, 100_000, 1, reference: reference);
        Assert.Equal(8, result.Checkpoints[0].Units[0].Stats[(int)Stat.Burn]); // Base 4 + 2 per different trinket.
        Assert.True(result.Checkpoints[^1].OutputTotals["Burn"] > 0);
    }

    [Fact]
    public void Combat_reference_cannot_be_used_for_training_and_unknown_combat_rules_still_block()
    {
        var rules = Rules();
        Assert.True(rules.CombatOnly); Assert.False(rules.Ready);
        Assert.Throws<InvalidOperationException>(() => new GameEnvironment(rules).Reset(1, "witch"));
        var invalid = rules with { Issues = [new("unknown", "Ability", "Missing handler", "fixture")] };
        Assert.False(invalid.CombatReady);
        Assert.Throws<InvalidOperationException>(() => invalid.RequireCombatReady());
    }

    [Fact]
    public async Task Shop_only_trainer_exemption_requires_the_exact_reviewed_text()
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var knowledge = JsonSerializer.Deserialize<StrategyKnowledge>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "assets", "wiki", "strategy-knowledge.json")))!;
        catalog = catalog with { Trainers = catalog.Trainers.Select(t => t.Id == "witch"
            ? t with { RuleText = "Give all monsters a previously unknown combat bonus." } : t).ToImmutableArray() };
        var rules = RuleCompiler.CompileCombat(catalog, knowledge);
        Assert.Contains(rules.Issues, issue => issue.Id == "witch");
        Assert.False(rules.CombatReady);
    }
}
