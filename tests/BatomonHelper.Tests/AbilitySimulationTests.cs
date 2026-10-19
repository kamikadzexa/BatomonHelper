using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class AbilitySimulationTests
{
    [Theory]
    [InlineData("This and Common allies gain +10 Damage permanently.",Trigger.BattleStart,Operation.Add,Target.All,10)]
    [InlineData("Give adjacent allies +40% Shield for this battle.",Trigger.Cast,Operation.Multiply,Target.Adjacent,.4)]
    [InlineData("Give the Flying ally in front +3 Multicast permanently. (Zephyrex can't have Multicast)",Trigger.Cast,Operation.Add,Target.Right,3)]
    [InlineData("Gain 3 random non-unique Legendary Trinkets.",Trigger.Victory,Operation.GrantTrinket,Target.Self,3)]
    [InlineData("Gain a Junk Trinket.",Trigger.Bought,Operation.GrantTrinket,Target.Self,1)]
    [InlineData("Charge adjacent Electric allies by 2 second(s). (Ironcore can't receive charge)",Trigger.Cast,Operation.Charge,Target.Adjacent,2)]
    [InlineData("Whenever an ally inflicts Poison, Charge this by 1 second(s).",Trigger.Ongoing,Operation.Charge,Target.Self,1)]
    [InlineData("Activate the On Victory ability of the ally above.",Trigger.Cast,Operation.ActivateAbility,Target.Above,1)]
    public void Anchored_grammar_preserves_numeric_values_targeting_and_rejects_unknown_suffixes(string text,Trigger trigger,Operation operation,Target target,double amount)
    {
        var effects=RuleCompiler.CompileText(text,trigger,out var understood);Assert.True(understood);
        var effect=Assert.Single(effects);Assert.Equal(operation,effect.Operation);Assert.Equal(target,effect.Target);Assert.Equal(amount,effect.Amount,8);
        RuleCompiler.CompileText(text+" Also perform an unknown action.",trigger,out understood);Assert.False(understood);
    }

    [Fact]
    public void Self_and_rarity_targeting_are_distinct_and_item_reactions_persist()
    {
        var basis=RuleCompiler.Fixture();
        var effects=RuleCompiler.CompileText("This and Common allies gain +10 Damage permanently.",Trigger.BattleStart,out var understood);Assert.True(understood);
        var actor=basis.Species[0] with {Rarity=3,Normal=basis.Species[0].Normal.Select(x=>x with {Effects=effects}).ToArray()};
        var common=basis.Species[1] with {Id="common",Rarity=0};var rare=common with {Id="rare",Rarity=2};
        var rules=basis with {Species=[actor,common,rare]};
        var board=new FrozenBoard(1,1000,"fixture",[new(){SpeciesId=actor.Id},new(){SpeciesId=common.Id},new(){SpeciesId=rare.Id},null,null,null],[],[]);
        var battle=new Combat(rules).ResolveBattle(board,board,1);
        Assert.Equal(10,battle.PersistentTeam[0]!.Bonuses[0]);Assert.Equal(10,battle.PersistentTeam[1]!.Bonuses[0]);Assert.Equal(0,battle.PersistentTeam[2]!.Bonuses[0]);
        var reaction=RuleCompiler.CompileText("When you use an item, this gains +24 Damage and Shield.",Trigger.Ongoing,out understood);
        actor=actor with {Normal=actor.Normal.Select(x=>x with {Effects=reaction}).ToArray(),Rarity=0};
        var item=ItemRules.Definition("fake-coin")!.Value;
        rules=rules with {Species=[actor],Items=[new("fake-coin","Coin",0,false,0,item.Effects,"fixture",item.Text)]};
        var environment=new GameEnvironment(rules);environment.Reset(7,"fixture");environment.State.Units[0]=new(){SpeciesId=actor.Id};
        environment.State.Shop[4]=new(null,"fake-coin",0);environment.Step(new(Move.BuyItem,4));
        Assert.Equal(24,environment.State.Units[0]!.Bonuses[0]);Assert.Equal(24,environment.State.Units[0]!.Bonuses[2]);Assert.Equal(1,environment.State.FreeRerolls);
    }

    [Fact]
    public void Charge_discards_excess_progress_and_status_reactions_are_one_per_positive_cast()
    {
        var basis=RuleCompiler.Fixture();var stats=new double[9];stats[(int)Stat.Poison]=1;stats[(int)Stat.Multicast]=1;
        var caster=basis.Species[0] with {Id="caster",Normal=[new(1,1,stats,[])]};
        var charged=basis.Species[0] with {Id="charged",Normal=[new(1,2,basis.Species[0].Normal[0].Stats,
            [new(Trigger.AllyPoison,Operation.Charge,Target.Self,Amount:3)])]};
        var rules=basis with {Species=[caster,charged]};
        var own=new FrozenBoard(1,500,"fixture",[new(){SpeciesId=caster.Id},new(){SpeciesId=charged.Id},null,null,null,null],[],[]);
        var enemy=own with {Team=[null,null,null,null,null,null]};
        var fast=new Combat(rules).ResolveBattle(own,enemy,1,true);var slow=new Combat(rules).ResolveBattle(own,enemy,1,true,true);
        Assert.Equal(slow.Trace,fast.Trace);Assert.Contains(fast.Trace,x=>x=="1.01s side 0 slot 1: charged cast");
        Assert.Equal(1,fast.Trace.Count(x=>x.StartsWith("1.01s side 0 slot 1")));
        Assert.DoesNotContain(fast.Trace,x=>x=="1.02s side 0 slot 1: charged cast");
    }

    [Fact]
    public void Trainer_victory_effects_update_the_player_once_and_leave_the_opponent_snapshot_unchanged()
    {
        var basis=RuleCompiler.Fixture();var effects=RuleCompiler.CompileText("On Victory Give your Fire monsters +3 Burn permanently.",Trigger.Ongoing,out var understood);Assert.True(understood);
        var trainer=new InventoryDefinition("redhead","Redhead",0,false,0,effects,"fixture","");
        var rules=basis with {Trainers=basis.Trainers.Append(trainer).ToArray()};
        var own=new FrozenBoard(1,200,"redhead",[new(){SpeciesId="striker"},new(){SpeciesId="guard"},null,null,null,null],[],[]);
        var enemy=own with {MaxHp=1,Team=[null,null,null,null,null,null]};var snapshot=JsonSerializer.Serialize(enemy);
        var result=new Combat(rules).ResolveBattle(own,enemy,1);
        Assert.True(result.PlayerWon);Assert.Equal(3,result.PersistentTeam[0]!.Bonuses[(int)Stat.Burn]);Assert.Equal(0,result.PersistentTeam[1]!.Bonuses[(int)Stat.Burn]);
        Assert.Equal(snapshot,JsonSerializer.Serialize(enemy));
    }
    [Fact]
    public void Simultaneous_queued_knockouts_follow_player_first_priority()
    {
        var basis=RuleCompiler.Fixture();var stats=new double[9];stats[0]=100;stats[7]=1;
        var striker=basis.Species[0] with {Normal=[new(1,1,stats,[new(Trigger.Cast,Operation.Knockout,Target.Opposite)])]};
        var rules=basis with {Species=[striker]};
        var board=new FrozenBoard(1,50,"fixture",[new(){SpeciesId=striker.Id},null,null,null,null,null],[],[]);
        var result=new Combat(rules).ResolveBattle(board,board,1,true);
        Assert.True(result.PlayerWon);Assert.False(result.BothDefeated);Assert.Equal(50,result.PlayerHp);Assert.Equal(1,result.Seconds);
        Assert.Single(result.Trace);Assert.Contains("side 0",result.Trace[0]);
    }
    [Fact]
    public void Battle_only_level_and_type_changes_expire_but_permanent_gains_survive()
    {
        var basis=RuleCompiler.Fixture();
        var species=basis.Species[0] with {Normal=basis.Species[0].Normal.Select(x=>x with {Effects=[
            new(Trigger.BattleStart,Operation.LevelUp,Target.Self),new(Trigger.BattleStart,Operation.AddType,Target.Self,Type:"toxic"),
            new(Trigger.BattleStart,Operation.Add,Target.Self,Amount:7,Permanent:true)]}).ToArray()};
        var rules=basis with {Species=[species]};
        var own=new FrozenBoard(1,20,"fixture",[new(){SpeciesId=species.Id},null,null,null,null,null],[],[]);
        var result=new Combat(rules).ResolveBattle(own,own,1);
        Assert.Equal(1,result.PersistentTeam[0]!.Level);Assert.Empty(result.PersistentTeam[0]!.AddedTypes);Assert.Equal(7,result.PersistentTeam[0]!.Bonuses[0]);
        Assert.Equal(2,result.Seconds);
    }

    [Fact]
    public void Revival_does_not_restore_multicasts_queued_before_knockout()
    {
        var basis=RuleCompiler.Fixture();var stats=new double[9];stats[0]=1;stats[7]=3;
        var sacrifice=basis.Species[0] with {Id="sacrifice",Normal=[new(1,1,stats,[new(Trigger.Cast,Operation.Knockout,Target.Self)])]};
        stats=[0,0,0,0,0,0,0,1,0];
        var reviver=basis.Species[0] with {Id="reviver",Normal=[new(1,1.05,stats,[new(Trigger.Cast,Operation.Revive,Target.Adjacent)])]};
        var rules=basis with {Species=[sacrifice,reviver]};
        var own=new FrozenBoard(1,100,"fixture",[new(){SpeciesId=sacrifice.Id},new(){SpeciesId=reviver.Id},null,null,null,null],[],[]);
        var result=new Combat(rules).ResolveBattle(own,own with {Team=[null,null,null,null,null,null]},1,true);
        Assert.Contains(result.Trace,x=>x=="1.00s side 0 slot 0: sacrifice cast");Assert.Contains(result.Trace,x=>x=="1.05s side 0 slot 1: reviver cast");
        Assert.DoesNotContain(result.Trace,x=>x=="1.10s side 0 slot 0: sacrifice cast");
        Assert.DoesNotContain(result.Trace,x=>x=="1.20s side 0 slot 0: sacrifice cast");
    }

    [Fact]
    public void Non_unique_grants_respect_rarity_order_quantities_and_frozen_opponents()
    {
        var basis=RuleCompiler.Fixture();var effect=RuleCompiler.CompileText("Gain 3 random non-unique Common Trinkets.",Trigger.Victory,out var understood);Assert.True(understood);
        var species=basis.Species[0] with {Normal=basis.Species[0].Normal.Select(x=>x with {Effects=effect}).ToArray()};
        var rules=basis with {Species=[species],Trinkets=[new("good","Good",0,false,0,[],"fixture",""),new("unique","Unique",0,true,0,[],"fixture","")]};
        var own=new FrozenBoard(1,1,"fixture",[new(){SpeciesId=species.Id},null,null,null,null,null],[],[]);var saved=JsonSerializer.Serialize(own);
        var result=new Combat(rules).ResolveBattle(own,own,1);
        Assert.Equal(3,result.Bag.Length);Assert.All(result.Bag,x=>Assert.Equal("good",x.Id));Assert.All(result.Bag,x=>Assert.Equal(1,x.Quantity));
        Assert.Equal(saved,JsonSerializer.Serialize(own));
    }
}
