using BatomonHelper.Simulation;
using System.Text.Json;
using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class Balance28CombatTests
{
    private static RulesManifest Rules() => JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "combat-rules.json")))!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Simultaneous_poison_casts_use_each_sides_game_order(bool reference)
    {
        var rules = Rules();
        var player = new FrozenBoard(1, 1000, "pyromaniac",
            [null, MakeUnit("noxnimbus"), MakeUnit("venopuff"), null, null, null], [], []);
        var opponent = player with
        {
            Team = [null, MakeUnit("venopuff"), null, null, MakeUnit("noxnimbus"), null],
            Mirrored = true
        };
        var frames = new List<GameBattleFrame>();
        new Combat(rules).ResolveBattle(player, opponent, 69, reference: reference, audit: frames.Add);
        var firstCast = frames.Single(frame => frame.ElapsedSeconds == 3.5);
        Assert.Equal(5, firstCast.Opponent.Poison);
        Assert.Equal(8, firstCast.Player.Poison);
        Assert.Equal(7, firstCast.Player.Units.Single(unit => unit.Slot == 2).Stats[(int)Stat.Poison]);
    }

    [Fact]
    public void Start_buff_reaches_a_later_ability_in_game_order()
    {
        var fixture = RuleCompiler.Fixture();
        var buff = fixture.Species[0] with
        {
            Normal = [new(1, 1, new double[9], [new(Trigger.BattleStart, Operation.Add, Target.Above, Stat.Damage, 12)])]
        };
        var copy = fixture.Species[1] with
        {
            Normal = [new(1, 1, new double[9], [new(Trigger.BattleStart, Operation.CopyStat, Target.Self,
                Stat.Shield, 1, ScaleStat: Stat.Damage)])]
        };
        var rules = fixture with { Build = "26773903", Species = [buff, copy] };
        var player = new FrozenBoard(1, 100, "fixture",
            [null, MakeUnit(copy.Id), null, null, MakeUnit(buff.Id), null], [], []);
        var frames = new List<GameBattleFrame>();
        new Combat(rules).ResolveBattle(player, player with { Team = new Unit?[6] }, 69, audit: frames.Add);
        Assert.Equal(12, frames[0].Player.Units.Single(unit => unit.Slot == 1).Stats[(int)Stat.Shield]);
    }

    [Fact]
    public void Overflow_fills_one_cooldown_and_triggered_multicasts_share_cadence()
    {
        var fixture = RuleCompiler.Fixture();
        var source = fixture.Species[0] with
        {
            Normal = [new(1, 1, [0, 0, 0, 0, 0, 0, 0, 1, 0],
                [new(Trigger.Cast, Operation.Charge, Target.Right, Amount: 3),
                 new(Trigger.Cast, Operation.Charge, Target.Right, Amount: 4),
                 new(Trigger.Cast, Operation.Trigger, Target.Right)])]
        };
        var recipient = fixture.Species[1] with { Normal = [new(1, 2, [1, 0, 0, 0, 0, 0, 0, 2, 0], [])] };
        var rules = fixture with { Build = "26773903", Species = [source, recipient] };
        var player = new FrozenBoard(1, 100, "fixture",
            [MakeUnit(source.Id), MakeUnit(recipient.Id), null, null, null, null], [], []);
        var opponent = player with { Team = new Unit?[6] };
        var fast = new List<GameBattleFrame>();
        var reference = new List<GameBattleFrame>();
        var resolver = new Combat(rules);
        resolver.ResolveBattle(player, opponent, 69, audit: fast.Add);
        resolver.ResolveBattle(player, opponent, 69, reference: true, audit: reference.Add);

        var overlap = fast.Single(frame => frame.ElapsedSeconds == 1).Player.Units.Single(unit => unit.Slot == 1);
        Assert.Equal(2, overlap.CooldownProgressSeconds);
        Assert.Equal(1, overlap.PendingCasts);
        var casts = CompletedCastTimes(reference);
        Assert.Equal(new[] { 1d, 34d / 30, 38d / 30, 42d / 30 }, casts);
        Assert.Equal(casts, CompletedCastTimes(fast));

        static double[] CompletedCastTimes(List<GameBattleFrame> frames) => frames
            .Where(frame => frame.ElapsedSeconds < 2)
            .DistinctBy(frame => frame.Player.Units.Single(unit => unit.Slot == 1).Casts)
            .Skip(1).Select(frame => Math.Round(frame.ElapsedSeconds * 30) / 30).ToArray();
    }

    [Fact]
    public void Oniclaw_burn_bonuses_add_and_giant_club_uses_adjusted_cooldown()
    {
        var rules = Rules();
        var burner = RuleCompiler.Fixture().Species[0] with
        {
            Normal = [new(1, .6, [10, 0, 0, 1, 0, 0, 0, 1, 0], [])]
        };
        rules = rules with { Species = [.. rules.Species, burner] };
        var oniclaw = MakeUnit("oniclaw", 3);
        oniclaw.Bonuses[(int)Stat.Damage] = 60;
        oniclaw.Bonuses[(int)Stat.Speed] = .3;
        var player = new FrozenBoard(1, 1000, "pyromaniac",
            [oniclaw, MakeUnit(burner.Id), null, null, null, null], [new("giant-club", 1)], []);
        var frames = new List<GameBattleFrame>();
        new Combat(rules).ResolveBattle(player, player with { Team = new Unit?[6] }, 69, audit: frames.Add);
        var initial = frames[0].Player.Units.Single(unit => unit.Slot == 0);
        Assert.Equal(4.6, initial.EffectiveCooldownSeconds);
        Assert.Equal(160, initial.Stats[(int)Stat.Damage]);
        var twice = frames.Single(frame => frame.ElapsedSeconds == 1.2).Player.Units.Single(unit => unit.Slot == 0);
        Assert.Equal(640, twice.Stats[(int)Stat.Damage]);
    }

    private static Unit MakeUnit(string species, int level = 1) => new() { SpeciesId = species, Level = level };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_trainer_and_balance_values_work_in_live_projections(bool reference)
    {
        var rules = Rules();
        Assert.Equal("26773903", rules.Build); Assert.Equal("28", rules.Balance);
        Assert.Equal(453800, rules.Parameters.DailyHp[19]);
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
