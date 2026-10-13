using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class EvolutionSimulationTests
{
    private static Effect[] Effects(string text,Trigger trigger=Trigger.None)
    {var effects=RuleCompiler.CompileText(text,trigger,out var understood);Assert.True(understood);return effects;}
    private static Species Species(string id,Effect[] effects,string? next=null,double damage=10) => RuleCompiler.Fixture().Species[0] with
    {Id=id,MergeGroup=id,EvolutionId=next,EvolutionLevel=null,Normal=Enumerable.Range(1,4).Select(l=>new LevelDefinition(l,1,[damage,0,0,0,0,0,0,1,0],effects)).ToArray()};
    private static FrozenBoard Board(string id) => new(1,300,"fixture",[new(){SpeciesId=id,Instance=50},null,null,null,null,null],[],[]);
    private static BattleResult Match(RulesManifest rules,FrozenBoard own,FrozenBoard enemy)
    {
        var snapshot=JsonSerializer.Serialize(enemy);var combat=new Combat(rules);var fast=combat.ResolveBattle(own,enemy,1,true);
        var slow=combat.ResolveBattle(own,enemy,1,true,true);
        Assert.Equal(slow.Trace,fast.Trace);Assert.Equal(slow.PlayerHp,fast.PlayerHp,7);Assert.Equal(slow.OpponentHp,fast.OpponentHp,7);
        Assert.Equal(snapshot,JsonSerializer.Serialize(enemy));return fast;
    }
    [Theory]
    [InlineData("Evolve.")]
    [InlineData("Evolve after your team deals 20 non-status Damage.")]
    [InlineData("Evolve when your team inflicts Burn 2 times.")]
    [InlineData("Evolve after collecting 2 more Trinkets.")]
    [InlineData("Evolve after this monster experiences Knockout 3 times.")]
    [InlineData("Hatches a Legendary Dragon monster in 2 days.")]
    [InlineData("When this triggers, gain a random non-unique Common Trinket, then improve this ability for this battle.")]
    public void Grammars_are_anchored(string text)
    {Assert.NotEmpty(Effects(text));RuleCompiler.CompileText(text+" Unknown.",Trigger.None,out var understood);Assert.False(understood);}

    [Fact]
    public void Victory_evolves_one_form_per_win_and_preserves_identity_types_stats_and_counters()
    {
        var basis=RuleCompiler.Fixture();var first=Species("first",Effects("Evolve.",Trigger.Victory),"middle");
        var middle=Species("middle",Effects("Evolve.",Trigger.Victory),"final");var final=Species("final",[]);
        var rules=basis with {Species=[first,middle,final]};var own=Board("first");own.Team[0]!.Shiny=true;
        own.Team[0]!.AddedTypes=["water"];own.Team[0]!.Bonuses[0]=7;own.Team[0]!.Counters["saved"]=8;
        var result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("middle",result.PersistentTeam[0]!.SpeciesId);Assert.Equal(50,result.PersistentTeam[0]!.Instance);
        Assert.True(result.PersistentTeam[0]!.Shiny);Assert.Equal(["water"],result.PersistentTeam[0]!.AddedTypes);
        Assert.Equal(7,result.PersistentTeam[0]!.Bonuses[0]);Assert.Equal(8,result.PersistentTeam[0]!.Counters["saved"]);
        own=own with {Team=result.PersistentTeam};result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("final",result.PersistentTeam[0]!.SpeciesId);
    }

    [Fact]
    public void Direct_damage_progress_excludes_status_damage_and_is_preserved_across_battles()
    {
        var basis=RuleCompiler.Fixture();var first=Species("first",Effects("Evolve after your team deals 20 non-status Damage."),"final");
        var final=Species("final",[]);var rules=basis with {Species=[first,final]};var own=Board("first");
        var result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("first",result.PersistentTeam[0]!.SpeciesId);Assert.Equal(10,result.PersistentTeam[0]!.Counters["evolution:first:damage"]);
        own=own with {Team=result.PersistentTeam};result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("final",result.PersistentTeam[0]!.SpeciesId);Assert.Equal(20,result.PersistentTeam[0]!.Counters["evolution:first:damage"]);
        first=first with {Normal=first.Normal.Select(x=>x with {Stats=[0,0,0,0,100,0,0,1,0]}).ToArray()};
        rules=rules with {Species=[first,final]};own=Board("first");result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("first",result.PersistentTeam[0]!.SpeciesId);Assert.False(result.PersistentTeam[0]!.Counters.ContainsKey("evolution:first:damage"));
    }

    [Fact]
    public void Burn_progress_counts_applications_rather_than_stacks()
    {
        var basis=RuleCompiler.Fixture();var first=Species("first",Effects("Evolve when your team inflicts Burn 2 times."),"final",0);
        first=first with {Normal=first.Normal.Select(x=>x with {Stats=[0,0,0,10,0,0,0,1,0]}).ToArray()};
        var final=Species("final",[]);var rules=basis with {Species=[first,final]};var own=Board("first");
        var result=Match(rules,own,own with {MaxHp=25,Team=new Unit?[6]});
        Assert.Equal(2,result.Seconds);Assert.Equal("final",result.PersistentTeam[0]!.SpeciesId);
        Assert.Equal(2,result.PersistentTeam[0]!.Counters["evolution:first:burn"]);
    }

    [Fact]
    public void Trinket_progress_applies_to_bench_creatures_and_evolves_after_the_threshold()
    {
        var basis=RuleCompiler.Fixture();var first=Species("first",Effects("Evolve after collecting 2 more Trinkets."),"final");var final=Species("final",[]);
        var gift=new InventoryDefinition("gift","Gift",0,false,0,[],"fixture","");
        var item=new InventoryDefinition("item","Item",0,false,0,[new(Trigger.Bought,Operation.GrantTrinket,Target.All,DefinitionId:"gift")],"fixture","");
        var rules=basis with {Species=[first,final],Trinkets=[gift],Items=[item]};var env=new GameEnvironment(rules);env.Reset(123,"fixture");
        env.State.Units[6]=new(){SpeciesId="first",Instance=50};env.State.ItemUses=2;
        for(var i=0;i<2;i++) {env.State.Shop[4]=new(null,"item",0);env.Step(new(Move.BuyItem,4));}
        Assert.Equal("final",env.State.Units[6]!.SpeciesId);Assert.Equal(2,env.State.Units[6]!.Counters["evolution:first:trinkets"]);
    }

    [Fact]
    public void Knockout_evolution_waits_for_shop_start_and_for_an_existing_evolution_to_be_sold()
    {
        var basis=RuleCompiler.Fixture();var first=Species("first",Effects("Knockout self.",Trigger.Cast)
            .Concat(Effects("Evolve after this monster experiences Knockout 3 times.")).ToArray(),"final");var final=Species("final",[]);
        var env=new GameEnvironment(basis with {Species=[first,final]});env.Reset(123,"fixture");
        env.State.Units[0]=new(){SpeciesId="first",Instance=50};env.State.Units[6]=new(){SpeciesId="final",Instance=51};
        for(var i=0;i<3;i++)env.Step(new(Move.Finish),b=>b with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("first",env.State.Units[0]!.SpeciesId);Assert.Equal(3,env.State.Units[0]!.Counters["evolution:first:knockouts"]);
        env.Step(new(Move.Sell,6));env.Step(new(Move.Finish),b=>b with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal("final",env.State.Units[0]!.SpeciesId);Assert.Equal(50,env.State.Units[0]!.Instance);
    }

    [Fact]
    public void Hatching_counts_days_after_acquisition_and_preserves_identity_and_bonuses()
    {
        var basis=RuleCompiler.Fixture();var egg=Species("egg",Effects("Hatches a Legendary Dragon monster in 2 days."));
        var dragon=Species("dragon",[]) with {Rarity=4,Types=["dragon"]};
        var env=new GameEnvironment(basis with {Species=[egg,dragon]});env.Reset(123,"fixture");
        env.State.PendingShopStart=true;env.State.Units[0]=new(){SpeciesId="egg",Instance=50,Counters=new(){["born-day"]=1},Bonuses=[7,0,0,0,0,0,0,0,0]};
        // Drain the same-day shop start through a legal synthetic encounter choice.
        env.State.Choices=[new("Continue",null,[])];env.State.ActiveChoicePhase=env.State.Phase=Phase.Event;env.Step(new(Move.Choose,Choice:0));
        Assert.False(env.State.Units[0]!.Counters.ContainsKey("hatch:egg"));
        env.Step(new(Move.Finish),b=>b with {MaxHp=1,Team=new Unit?[6]});Assert.Equal("egg",env.State.Units[0]!.SpeciesId);
        env.Step(new(Move.Finish),b=>b with {MaxHp=1,Team=new Unit?[6]});Assert.Equal("dragon",env.State.Units[0]!.SpeciesId);
        Assert.Equal(50,env.State.Units[0]!.Instance);Assert.Equal(7,env.State.Units[0]!.Bonuses[0]);
    }

    [Fact]
    public void Growing_gifts_increase_each_cast_and_growth_expires_after_battle()
    {
        var basis=RuleCompiler.Fixture();var actor=Species("actor",Effects("When this triggers, gain a random non-unique Common Trinket, then improve this ability for this battle."),damage:1);
        var gift=new InventoryDefinition("gift","Gift",0,false,0,[],"fixture","");var rules=basis with {Species=[actor],Trinkets=[gift]};
        var own=Board("actor");var result=Match(rules,own,own with {MaxHp=3,Team=new Unit?[6]});
        Assert.Equal(6,result.Bag.Length);Assert.Empty(result.PersistentTeam[0]!.Counters);
        own=own with {Team=result.PersistentTeam,Bag=result.Bag};result=Match(rules,own,own with {MaxHp=3,Team=new Unit?[6]});
        Assert.Equal(12,result.Bag.Length);
    }
}
