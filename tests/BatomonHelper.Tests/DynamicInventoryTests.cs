using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class DynamicInventoryTests
{
    public static IEnumerable<object[]> Definitions => new[] { "haste-crown", "power-crown", "winged-crown", "master-crown", "quick-flag",
        "speed-crest", "rally-flag", "momentum-flag", "earth-crest", "sea-crest", "tempo-charm", "gold-bracelet", "winged-fossil",
        "fancy-sword", "scrap-sword", "rainbow-berry", "metronome", "repeater-charm", "link-cable" }.Select(x => new object[] { x });
    private static InventoryDefinition Inventory(string id)
    { var d = InventoryRules.Definition(id)!.Value; return new(id, id, 0, false, 0, d.Effects, "fixture", d.Text); }
    private static Species Species(RulesManifest basis, string id, string text, Trigger trigger, string[]? types = null)
    {
        var effects = RuleCompiler.CompileText(text, trigger, out var understood); Assert.True(understood);
        return basis.Species[0] with { Id = id, MergeGroup = id, Types = types ?? ["fire"],
            Normal = basis.Species[0].Normal.Select(x => x with { Effects = effects }).ToArray() };
    }
    private static FrozenBoard Board(params string[] ids) => new(1, 300, "fixture",
        Enumerable.Range(0, 6).Select(i => i < ids.Length ? new Unit { SpeciesId = ids[i], Instance = i + 1 } : null).ToArray(), [], []);
    private static BattleResult Match(RulesManifest rules, FrozenBoard own, FrozenBoard? enemy = null)
    {
        enemy ??= own with { Team = new Unit?[6] };
        var original = JsonSerializer.Serialize(own); var combat = new Combat(rules);
        var optimized = combat.ResolveBattle(own, enemy, 1, true); var reference = combat.ResolveBattle(own, enemy, 1, true, true);
        Assert.Equal(reference.Trace, optimized.Trace); Assert.Equal(reference.PlayerHp, optimized.PlayerHp, 7);
        Assert.Equal(reference.OpponentHp, optimized.OpponentHp, 7); Assert.Equal(reference.Seconds, optimized.Seconds);
        Assert.Equal(original, JsonSerializer.Serialize(own)); return optimized;
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_inventory_handler_is_anchored_and_executes_in_optimized_and_reference_combat(string id)
    {
        var definition = Inventory(id); Assert.True(InventoryRules.TryCompile(id, definition.Description, out _));
        Assert.False(InventoryRules.TryCompile(id, definition.Description + " Unknown.", out _));
        var basis = RuleCompiler.Fixture(); var flying = basis.Species[0] with { Types = ["flying"] };
        var rules = basis with { Species = [flying], Trinkets = [definition] };
        Match(rules, Board(flying.Id, flying.Id, flying.Id, flying.Id, flying.Id, flying.Id) with { Bag = [new(id, 2, 1)] });
        if (id == "gold-bracelet")
        {
            var env = new GameEnvironment(rules); env.Reset(123, "fixture"); env.State.Bag = [new(id, 1, 1)];
            env.State.Units[6] = new() { SpeciesId = flying.Id, Instance = 50 };
            env.Step(new(Move.Finish), b => b); Assert.Equal(8, env.State.Units[6]!.Bonuses[8]);
        }
    }

    [Theory]
    [InlineData("Has an additional +5 Burn for each Trinket that you own.", Trigger.Ongoing, EffectScale.Trinkets)]
    [InlineData("Has an additional +2 Burn for each different Trinket that you own.", Trigger.Ongoing, EffectScale.DistinctTrinkets)]
    [InlineData("Has an additional +160 Damage and +160 Heal for each ally of level 3 or above.", Trigger.Ongoing, EffectScale.Allies)]
    [InlineData("+4 Damage permanently for each unique type on your team.", Trigger.Cast, EffectScale.UniqueTeamTypes)]
    [InlineData("+3 Damage and +3 Shield permanently for each Trinket that you own.", Trigger.Cast, EffectScale.Trinkets)]
    [InlineData("Has additional Burn equal to 50% of the Burn stacks on the enemy.", Trigger.Ongoing, EffectScale.EnemyStatus)]
    [InlineData("Has additional Damage equal to 3 times this monster's Burn.", Trigger.Ongoing, EffectScale.SelfStat)]
    [InlineData("Adjacent allies have +0.5% Cooldown Speed for each Trinket that you own.", Trigger.Ongoing, EffectScale.Trinkets)]
    [InlineData("For each Fire ally, this gains +1% Cooldown Speed permanently.", Trigger.Cast, EffectScale.Allies)]
    [InlineData("+5 Shield permanently for each Rock ally and +1 Multicast permanently for each Flying ally.", Trigger.BattleStart, EffectScale.Allies)]
    public void Scaled_grammar_preserves_the_basis_and_rejects_unparsed_suffixes(string text, Trigger trigger, EffectScale scale)
    {
        var effects = RuleCompiler.CompileText(text, trigger, out var understood); Assert.True(understood);
        Assert.All(effects, x => Assert.Equal(scale, x.Scale));
        RuleCompiler.CompileText(text + " Unknown.", trigger, out understood); Assert.False(understood);
    }

    [Fact]
    public void Counts_include_quantities_and_unique_team_types_and_exclude_the_source_from_allies()
    {
        var basis = RuleCompiler.Fixture();
        var actor = Species(basis, "actor", "+5 Shield permanently for each Rock ally and +1 Multicast permanently for each Flying ally.", Trigger.BattleStart, ["rock", "flying"]);
        var ally = basis.Species[1] with { Types = ["rock", "flying"] };
        var rules = basis with { Species = [actor, ally] }; var own = Board(actor.Id, ally.Id);
        own.Team[1]!.AddedTypes = ["rock"]; var result = Match(rules, own);
        Assert.Equal(5, result.PersistentTeam[0]!.Bonuses[2]); Assert.Equal(1, result.PersistentTeam[0]!.Bonuses[7]);
        actor = Species(basis, "actor", "+4 Damage permanently for each unique type on your team.", Trigger.BattleStart, ["rock", "flying"]);
        rules = rules with { Species = [actor, ally] }; own = Board(actor.Id, ally.Id); own.Team[1]!.AddedTypes = ["fire", "rock"];
        result = Match(rules, own); Assert.Equal(12, result.PersistentTeam[0]!.Bonuses[0]);
        actor = Species(basis, "actor", "+3 Damage and +3 Shield permanently for each Trinket that you own.", Trigger.BattleStart);
        rules = rules with { Species = [actor], Trinkets = [new("junk", "Junk", 0, false, 0, [], "fixture", "")] };
        result = Match(rules, Board(actor.Id) with { Bag = [new("junk", 2, 1), new("junk", 3, 2)] });
        Assert.Equal(15, result.PersistentTeam[0]!.Bonuses[0]); Assert.Equal(15, result.PersistentTeam[0]!.Bonuses[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Furnadon_counts_different_trinkets_rather_than_duplicate_copies(bool reference)
    {
        var basis = RuleCompiler.Fixture();
        var actor = Species(basis, "furnadon", "Has an additional +2 Burn for each different Trinket that you own.", Trigger.Ongoing);
        var rules = basis with { Species = [actor], Trinkets = [
            new("junk", "Junk", 0, false, 0, [], "fixture", ""),
            new("other", "Other", 0, false, 0, [], "fixture", "")] };
        var board = Board(actor.Id) with { Bag = [new("junk", 4, 1), new("junk", 3, 2), new("other", 2, 3)] };
        var result = new BoardAnalyzer(rules).Analyze(board, 100_000, 1, reference: reference);
        Assert.Equal(4, result.Checkpoints[0].Units[0].Stats[(int)Stat.Burn]);
    }

    [Fact]
    public void Crowns_columns_and_permanent_percentages_use_the_correct_positions()
    {
        var basis = RuleCompiler.Fixture(); var stats = basis.Species[0].Normal.Select(x => x with { Stats = [25, 20, 10, 0, 0, 0, 0, 1, 0] }).ToArray();
        var rules = basis with { Species = [basis.Species[0] with { Normal = stats }],
            Trinkets = [Inventory("rally-flag"), Inventory("momentum-flag"), Inventory("earth-crest"), Inventory("sea-crest")] };
        var own = Board("striker", "striker", "striker", "striker", "striker", "striker") with
        { Bag = rules.Trinkets.Select((x, i) => new BagEntry(x.Id, 1, i + 1)).ToArray() };
        var result = Match(rules, own);
        foreach (var slot in new[] { 0, 3 }) { Assert.Equal(12, result.PersistentTeam[slot]!.Bonuses[0]); Assert.Equal(.08, result.PersistentTeam[slot]!.Bonuses[6]); }
        foreach (var slot in new[] { 2, 5 }) { Assert.Equal(1, result.PersistentTeam[slot]!.Bonuses[2]); Assert.Equal(2, result.PersistentTeam[slot]!.Bonuses[1]); }
        foreach (var slot in new[] { 1, 4 }) Assert.All(result.PersistentTeam[slot]!.Bonuses, x => Assert.Equal(0, x));
        Assert.Equal([1], Combat.Targets(0, Target.TopMiddle));
    }

    [Fact]
    public void Metronome_applies_after_output_and_expires_after_battle()
    {
        var basis = RuleCompiler.Fixture(); var rules = basis with { Trinkets = [Inventory("metronome")] };
        var own = Board("striker") with { Bag = [new("metronome", 1, 1)] };
        var result = Match(rules, own, own with { Bag = [], MaxHp = 30, Team = new Unit?[6] });
        Assert.Equal(4, result.Seconds); Assert.Equal(-30, result.OpponentHp); Assert.Equal(0, result.PersistentTeam[0]!.Bonuses[0]);
    }

    [Fact]
    public void Repeater_repeats_creature_abilities_and_inventory_start_effects_fire_once_per_copy()
    {
        var basis = RuleCompiler.Fixture(); var actor = Species(basis, "actor", "+7 Damage permanently.", Trigger.BattleStart);
        var rules = basis with { Species = [actor], Trinkets = [Inventory("repeater-charm"), Inventory("rally-flag")] };
        var own = Board("actor") with { Bag = [new("repeater-charm", 2, 1), new("rally-flag", 1, 2)] };
        Assert.Equal(33, Match(rules, own).PersistentTeam[0]!.Bonuses[0]);
    }

    [Fact]
    public void Link_cable_extends_adjacency_without_including_the_source()
    {
        var basis = RuleCompiler.Fixture(); var actor = Species(basis, "actor", "Give adjacent allies +10 Damage permanently.", Trigger.BattleStart);
        var rules = basis with { Species = [actor, basis.Species[1]], Trinkets = [Inventory("link-cable")] };
        var own = Board("actor") with { Bag = [new("link-cable", 1, 1)] }; own.Team[5] = new() { SpeciesId = "guard" };
        var result = Match(rules, own); Assert.Equal(0, result.PersistentTeam[0]!.Bonuses[0]); Assert.Equal(10, result.PersistentTeam[5]!.Bonuses[0]);
    }

    [Fact]
    public void Dynamic_self_stats_include_other_dynamic_stats_and_enemy_statuses_change_over_time()
    {
        var basis = RuleCompiler.Fixture(); var effects = RuleCompiler.CompileText("Has additional Damage equal to 3 times this monster's Burn.", Trigger.Ongoing, out _);
        effects = effects.Concat(RuleCompiler.CompileText("Has an additional +5 Burn for each Trinket that you own.", Trigger.Ongoing, out _)).ToArray();
        var actor = basis.Species[0] with { Normal = [new(1, 1, [0, 0, 0, 0, 0, 0, 0, 1, 0], effects)] };
        var rules = basis with { Species = [actor], Trinkets = [new("junk", "Junk", 0, false, 0, [], "fixture", "")] };
        var own = Board(actor.Id) with { Bag = [new("junk", 2, 1)] };
        var result = Match(rules, own, own with { Bag = [], MaxHp = 25, Team = new Unit?[6] });
        Assert.Equal(1, result.Seconds); Assert.Equal(-15, result.OpponentHp);
        var status = RuleCompiler.CompileText("Has additional Damage equal to 100% of the Poison stacks on the enemy.", Trigger.Ongoing, out _);
        actor = actor with { Normal = [new(1, 1, [0, 0, 0, 0, 10, 0, 0, 1, 0], status)] };
        rules = rules with { Species = [actor] }; own = Board(actor.Id);
        result = Match(rules, own, own with { MaxHp = 25, Team = new Unit?[6] });
        Assert.Equal(2, result.Seconds); Assert.Equal(-15, result.OpponentHp);
    }

    [Fact]
    public void Lost_lives_survive_second_chance_and_scale_later_purchases()
    {
        var basis = RuleCompiler.Fixture(); var actor = Species(basis, "actor", "+6 Damage permanently for each life lost this run.", Trigger.Bought);
        var rules = basis with { Species = [actor] }; var env = new GameEnvironment(rules); env.Reset(123, "fixture");
        env.State.Day = 5; env.State.Lives = 2;
        env.Step(new(Move.Finish), b => b with { Team = [new() { SpeciesId = "actor" }, null, null, null, null, null] });
        Assert.True(env.State.SecondChanceUsed); Assert.Equal(2, env.State.Counters["lives-lost"]);
        env.State.Shop[0] = new(new() { SpeciesId = "actor", Instance = 50 }, null, 0); env.Step(new(Move.Buy, 0, 0));
        Assert.Equal(12, env.State.Units[0]!.Bonuses[0]);
    }
}
