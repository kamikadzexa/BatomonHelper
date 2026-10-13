using BatomonHelper.Simulation;

namespace BatomonHelper.Tests;

public sealed class LevelTimerAmplifierTests
{
    private static Effect[] Effects(string text,Trigger trigger=Trigger.Ongoing)
    {var effects=RuleCompiler.CompileText(text,trigger,out var understood);Assert.True(understood);return effects;}
    private static Species Species(string id,double? cooldown,Effect[] effects,double damage=1,Trigger trigger=Trigger.Ongoing) => RuleCompiler.Fixture().Species[0] with
    {Id=id,MergeGroup=id,Normal=Enumerable.Range(1,4).Select(l=>new LevelDefinition(l,cooldown,[damage*l,0,0,0,0,0,0,1,0],effects,AbilityTrigger:trigger)).ToArray()};
    private static FrozenBoard Board(params string?[] ids) => new(1,100,"fixture",Enumerable.Range(0,6).Select(i=>i<ids.Length && ids[i] is { } id?new Unit {SpeciesId=id,Instance=i+1}:null).ToArray(),[],[]);
    private static BattleResult Match(Species[] species,FrozenBoard own,FrozenBoard enemy)
    {
        var combat=new Combat(RuleCompiler.Fixture() with {Species=species});var fast=combat.ResolveBattle(own,enemy,1,true);
        var slow=combat.ResolveBattle(own,enemy,1,true,true);Assert.Equal(slow.Trace,fast.Trace);Assert.Equal(slow.PlayerHp,fast.PlayerHp,7);Assert.Equal(slow.OpponentHp,fast.OpponentHp,7);return fast;
    }
    [Theory]
    [InlineData("Level up. This can level up indefinitely.")]
    [InlineData("This only needs 2 copies to level up. This can level up indefinitely.")]
    [InlineData("Adjacent allies apply their Ongoing abilities 2 additional time(s).")]
    [InlineData("The ally behind applies its Ongoing abilities 2 additional time(s).")]
    [InlineData("Allies and enemies in this row have +3 Cooldown.")]
    [InlineData("Every 4.0 seconds, +1 Multicast for this battle.")]
    public void Grammar_rejects_unknown_suffixes(string text)
    {Assert.NotEmpty(Effects(text));RuleCompiler.CompileText(text+" Unknown.",Trigger.Ongoing,out var understood);Assert.False(understood);}

    [Fact]
    public void Unlimited_merges_use_two_copies_and_continue_past_level_four()
    {
        var unit=Species("unlimited",1,Effects("This only needs 2 copies to level up. This can level up indefinitely."));
        var rules=RuleCompiler.Fixture() with {Species=[unit]};var env=new GameEnvironment(rules);env.Reset(123,"fixture");
        for(var level=1;level<=5;level++)
        {
            env.State.Units[0]=new(){SpeciesId=unit.Id,Instance=50,Level=level};env.State.Units[1]=new(){SpeciesId=unit.Id,Instance=51,Level=level};
            Assert.DoesNotContain(env.GetLegalActions(),x=>x.Kind==Move.Merge);
            env.Step(new(Move.Move,0,1));
            Assert.Equal(level+1,env.State.Units[0]!.Level);Assert.Equal(1,env.State.Units[0]!.Copies);
        }
        Assert.Equal(6,LevelRules.Resolve(unit,6,false).Stats[0]);Assert.NotEmpty(new PolicyEncoding(rules).Observation(env.GetObservation()));
        env.State.Gold=100;env.Step(new(Move.Reroll));Assert.All(env.State.Shop,x=>Assert.Equal(unit.Id,x!.Creature!.SpeciesId));
    }

    [Fact]
    public void Victory_level_growth_is_persistent_beyond_imported_levels()
    {
        var unit=Species("unlimited",1,Effects("Level up. This can level up indefinitely.",Trigger.Victory),trigger:Trigger.Victory);
        var rules=RuleCompiler.Fixture() with {Species=[unit]};var own=Board(unit.Id);own.Team[0]!.Level=4;
        var result=new Combat(rules).ResolveBattle(own,own with {MaxHp=1,Team=new Unit?[6]},1);
        Assert.Equal(5,result.PersistentTeam[0]!.Level);Assert.Equal(5,LevelRules.Resolve(unit,5,true).Stats[0]);
    }

    [Fact]
    public void Timer_pulse_precedes_a_same_tick_cast_and_does_not_persist()
    {
        var unit=Species("timer",4,Effects("Every 4.0 seconds, +1 Multicast for this battle."));var own=Board(unit.Id);
        var result=Match([unit],own,own with {MaxHp=2,Team=new Unit?[6]});
        Assert.Equal(4.1,result.Seconds,8);Assert.Contains(result.Trace,x=>x.StartsWith("4.00s"));Assert.Contains(result.Trace,x=>x.StartsWith("4.10s"));
        Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[7]);
    }

    [Fact]
    public void Ongoing_amplification_repeats_flat_auras_and_adds_percentage_auras()
    {
        var amp=Species("amp",null,Effects("The ally behind applies its Ongoing abilities 1 additional time(s)."),0);
        var aura=Species("aura",null,Effects("Give adjacent allies +10 Damage.",Trigger.Ongoing),0);
        var caster=Species("caster",1,[],1,Trigger.None);var own=Board("amp",null,null,"aura","caster");
        var result=Match([amp,aura,caster],own,own with {MaxHp=20,Team=new Unit?[6]});Assert.Equal(-1,result.OpponentHp);
        aura=aura with {Normal=aura.Normal.Select(x=>x with {Effects=Effects("Give adjacent allies +100% Damage.",Trigger.Ongoing)}).ToArray()};
        result=Match([amp,aura,caster],own,own with {MaxHp=2,Team=new Unit?[6]});Assert.Equal(-1,result.OpponentHp);
    }

    [Fact]
    public void A_passive_event_reaction_is_amplified_without_amplifying_the_amplifier()
    {
        var amp=Species("amp",null,Effects("The ally behind applies its Ongoing abilities 1 additional time(s)."),0);
        var watcher=Species("watcher",null,Effects("When allies inflict Poison, this gains +2 Damage permanently."),0);
        var caster=Species("caster",1,[],0,Trigger.None);foreach(var level in caster.Normal)level.Stats[4]=1;
        var own=Board("amp","caster",null,"watcher");var result=Match([amp,watcher,caster],own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal(4,result.PersistentTeam[3]!.Bonuses[0]);
    }

    [Fact]
    public void Row_cooldown_changes_cover_both_boards_and_end_when_the_source_is_disabled()
    {
        var row=Species("row",null,Effects("Allies and enemies in this row have +3 Cooldown."),0);
        var caster=Species("caster",1,[],1,Trigger.None);var own=Board("row","caster");var enemy=Board("caster") with {MaxHp=1};
        var result=Match([row,caster],own,enemy);Assert.Equal(4,result.Seconds);Assert.StartsWith("4.00s",result.Trace[0]);
        var disable=Species("disable",null,Effects("Disable abilities of all Ongoing monsters in this row.",Trigger.BattleStart),0,Trigger.BattleStart);
        own=Board("row","caster","disable");result=Match([row,caster,disable],own,enemy);Assert.Equal(1,result.Seconds);
    }
    [Fact]
    public void Amplified_evolution_stops_applying_the_old_form_after_the_first_transformation()
    {
        var amp=Species("amp",null,Effects("The ally behind applies its Ongoing abilities 2 additional time(s)."),0);
        var first=Species("first",null,Effects("Evolve after your team deals 1 non-status Damage.",Trigger.None),0) with {EvolutionId="middle"};
        var middle=first with {Id="middle",EvolutionId="final"};var final=Species("final",null,[],0);
        var caster=Species("caster",1,[],1,Trigger.None);var own=Board("amp","caster",null,"first");
        var result=Match([amp,first,middle,final,caster],own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("middle",result.PersistentTeam[3]!.SpeciesId);
        Assert.False(result.PersistentTeam[3]!.Counters.ContainsKey("evolution:middle:damage"));
    }
}
