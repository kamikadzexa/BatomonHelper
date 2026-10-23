using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class KnockoutReactionTests
{
    private static Effect[] Effects(string text, Trigger trigger = Trigger.Cast)
    { var result = RuleCompiler.CompileText(text, trigger, out var understood); Assert.True(understood); return result; }
    private static Species Unit(string id, double? cooldown, Effect[] effects, double damage = 0) =>
        RuleCompiler.Fixture().Species[0] with { Id = id, MergeGroup = id,
            Normal = Enumerable.Range(1, 4).Select(l => new LevelDefinition(l, cooldown, [damage * l, 0, 0, 0, 0, 0, 0, 1, 0], effects)).ToArray() };
    private static FrozenBoard Board(params string?[] ids) => new(1, 300, "fixture",
        Enumerable.Range(0, 6).Select(i => i < ids.Length && ids[i] is { } id ? new BatomonHelper.Simulation.Unit { SpeciesId = id, Instance = i + 1 } : null).ToArray(), [], []);
    private static BattleResult Match(Species[] species, FrozenBoard own, FrozenBoard? enemy = null)
    {
        enemy ??= own with { Team = new BatomonHelper.Simulation.Unit?[6] };
        var snapshot = JsonSerializer.Serialize(enemy); var rules = RuleCompiler.Fixture() with { Species = species };
        var combat = new Combat(rules); var result = combat.ResolveBattle(own, enemy, 1, true);
        var reference = combat.ResolveBattle(own, enemy, 1, true, true);
        Assert.Equal(reference.Trace, result.Trace); Assert.Equal(reference.PlayerHp, result.PlayerHp, 7);
        Assert.Equal(reference.OpponentHp, result.OpponentHp, 7); Assert.Equal(snapshot, JsonSerializer.Serialize(enemy));
        return result;
    }

    [Theory]
    [InlineData("Knockout adjacent allies and gain +7 Poison this battle for each ally Knockout.")]
    [InlineData("Knockout adjacent allies and gain +9 Shield permanently for each ally Knockout.")]
    [InlineData("Knockout a random enemy.")]
    [InlineData("Knockout the monster opposite of this and gain their stats for this battle (except Cooldown).")]
    [InlineData("On Knockout of this or an ally, this gains +4 Burn permanently.")]
    [InlineData("On Knockout of any monster, this gains +8 Damage permanently.")]
    [InlineData("On ally knockout, this gains 50% of their Damage for this battle. (Except other Danuki)")]
    [InlineData("Knocked-out allies are revived and gain +50% Cooldown Speed for this battle.")]
    [InlineData("+1 Multicast permanently. Revives itself once per battle.")]
    [InlineData("On the first cast, level up the ally above for this battle (max level 4).")]
    [InlineData("Level up all adjacent allies for this battle (max level 4).")]
    [InlineData("Give the ally in front +1 Multicast and increase this monster's Cooldown by 0.1 seconds for this battle.")]
    [InlineData("Trigger this when an ally of level 3 or above casts. (Except other Snapscald)")]
    [InlineData("Trigger this when an ally applies Shield. (Except other Rhizuka)")]
    [InlineData("Trigger this when adjacent Toxic allies trigger. (Except other Puffloon)")]
    [InlineData("Trigger a random Rock ally. (Except other Opalion)")]
    [InlineData("Charge adjacent Fire and Water allies by 1 second(s). (Steamscuttle can't receive charge)")]
    public void Grammars_reject_unknown_suffixes(string text)
    {
        Assert.NotEmpty(Effects(text)); RuleCompiler.CompileText(text + " Unknown.", Trigger.Cast, out var understood); Assert.False(understood);
    }

    [Fact]
    public void Protect_prevents_knockout_bonuses_and_successful_knockouts_only_count_once()
    {
        var actor = Unit("actor", 1, Effects("Knockout adjacent allies and gain +9 Shield permanently for each ally Knockout."));
        var guarded = Unit("guard", null, [new(Trigger.BattleStart, Operation.Protect, Target.Self, Amount: 1)]);
        var victim = Unit("victim", null, []); var own = Board("actor", "guard", null, "victim");
        var result = Match([actor, guarded, victim], own, own with { MaxHp = 1, Team = new BatomonHelper.Simulation.Unit?[6] });
        // First cast knocks out one ally; second cast uses up the remaining unprotected ally.
        Assert.Equal(18, result.PersistentTeam[0]!.Bonuses[2]);
        var poison = actor with { Normal = actor.Normal.Select(x => x with { Effects = Effects("Knockout adjacent allies and gain +7 Poison this battle for each ally Knockout.") }).ToArray() };
        result = Match([poison, guarded, victim], own, own with { MaxHp = 5, Team = new BatomonHelper.Simulation.Unit?[6] });
        Assert.Equal(2, result.Seconds); Assert.Equal(-2, result.OpponentHp); Assert.Equal(0, result.PersistentTeam[0]!.Bonuses[4]);
    }

    [Fact]
    public void Own_ally_and_enemy_knockout_observers_are_distinct_and_dead_sources_keep_permanent_gains()
    {
        var self = Unit("self", 1, Effects("Knockout self.").Concat(Effects("On Knockout of this or an ally, this gains +4 Burn permanently.")).ToArray());
        var ally = Unit("ally", null, Effects("On Knockout of this or an ally, this gains +4 Burn permanently."));
        var any = Unit("any", null, Effects("On Knockout of any monster, this gains +8 Damage permanently."));
        var result = Match([self, ally, any], Board("self", "ally", "any"));
        Assert.Equal(4, result.PersistentTeam[0]!.Bonuses[3]); Assert.Equal(4, result.PersistentTeam[1]!.Bonuses[3]);
        Assert.Equal(8, result.PersistentTeam[2]!.Bonuses[0]);
        var killer = Unit("killer", 1, Effects("Knockout the enemy opposite of this."), 1);
        result = Match([killer, ally, any], Board("killer", "ally", "any"), Board("ally") with { MaxHp = 1 });
        Assert.Equal(0, result.PersistentTeam[1]!.Bonuses[3]); Assert.Equal(8, result.PersistentTeam[2]!.Bonuses[0]);
    }

    [Fact]
    public void Stealing_uses_pre_knockout_stats_for_the_next_cast_and_preserves_the_opponent()
    {
        var actor = Unit("actor", 1, Effects("Knockout the monster opposite of this and gain their stats for this battle (except Cooldown)."), 1);
        var victim = Unit("victim", 5, [], 10); victim.Normal[0].Stats[6] = 2; victim.Normal[0].Stats[7] = 3;
        var result = Match([actor, victim], Board("actor"), Board("victim") with { MaxHp = 10 });
        Assert.Equal(2, result.Seconds); Assert.Equal(-2, result.OpponentHp);
        Assert.All(result.PersistentTeam[0]!.Bonuses, x => Assert.Equal(0, x)); Assert.Equal(2, result.Trace.Length);
    }

    [Fact]
    public void Self_revival_is_once_per_battle_and_invalidates_previous_multicast_events()
    {
        var effects = Effects("Knockout self.").Concat(Effects("+1 Multicast permanently. Revives itself once per battle.", Trigger.BattleStart)).ToArray();
        var actor = Unit("actor", 1, effects); var result = Match([actor], Board("actor"));
        Assert.Equal(2, result.Trace.Count(x => x.Contains("side 0 slot 0")));
        Assert.Contains(result.Trace, x => x.StartsWith("1.00s")); Assert.Contains(result.Trace, x => x.StartsWith("2.00s"));
        Assert.DoesNotContain(result.Trace, x => x.StartsWith("1.10s") || x.StartsWith("2.10s"));
        Assert.Equal(1, result.PersistentTeam[0]!.Bonuses[7]);
    }

    [Fact]
    public void First_cast_level_up_does_not_repeat_on_later_casts_or_persist()
    {
        var actor = Unit("actor", 1, Effects("On the first cast, level up the ally above for this battle (max level 4)."));
        var above = Unit("above", 2, [], 25); var own = Board("above", null, null, "actor");
        var result = Match([actor, above], own, own with { MaxHp = 45, Team = new BatomonHelper.Simulation.Unit?[6] });
        Assert.Equal(2, result.Seconds); Assert.Equal(-5, result.OpponentHp); Assert.Equal(1, result.PersistentTeam[0]!.Level);
    }

    [Fact]
    public void Revival_bonuses_apply_only_to_dead_allies_and_do_not_persist()
    {
        var reviver = Unit("reviver", 1.5, Effects("Knocked-out allies are revived and gain +50% Cooldown Speed for this battle."));
        var sacrifice = Unit("sacrifice", 1, Effects("Knockout self.")); var alive = Unit("alive", 2, [], 1);
        var result = Match([reviver, sacrifice, alive], Board("reviver", "sacrifice", "alive"));
        Assert.Contains(result.Trace, x => x.StartsWith("2.17s side 0 slot 1"));
        Assert.Contains(result.Trace, x => x.StartsWith("4.00s side 0 slot 2"));
        Assert.Equal(0, result.PersistentTeam[1]!.Bonuses[6]);
    }

    [Fact]
    public void Reactive_casts_check_the_event_actors_level_and_species()
    {
        var watcher = Unit("snapscald", null, Effects("Trigger this when an ally of level 3 or above casts. (Except other Snapscald)"), 10);
        var caster = Unit("caster", 1, [], 1); var own = Board("caster", "snapscald", "snapscald"); own.Team[0]!.Level = 3;
        var result = Match([watcher, caster], own, own with { MaxHp = 22, Team = new BatomonHelper.Simulation.Unit?[6] });
        Assert.Equal(1, result.Seconds); Assert.Equal(3, result.Trace.Length);
        own.Team[0]!.Level = 2; result = Match([watcher, caster], own, own with { MaxHp = 1, Team = new BatomonHelper.Simulation.Unit?[6] });
        Assert.Single(result.Trace); Assert.DoesNotContain(result.Trace, x => x.Contains("snapscald"));
    }
}
