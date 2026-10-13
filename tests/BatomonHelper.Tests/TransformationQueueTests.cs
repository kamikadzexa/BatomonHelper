using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class TransformationQueueTests
{
    private static Effect[] Effects(string text,Trigger trigger=Trigger.Cast)
    {var effects=RuleCompiler.CompileText(text,trigger,out var understood);Assert.True(understood);return effects;}
    private static Species Species(string id,double cooldown,Effect[] effects,int rarity=0,double damage=10) => RuleCompiler.Fixture().Species[0] with
    {Id=id,MergeGroup=id,Rarity=rarity,Normal=Enumerable.Range(1,4).Select(l=>new LevelDefinition(l,cooldown,[damage*l,0,0,0,0,0,0,1,0],effects)).ToArray()};
    private static FrozenBoard Board(params string[] ids) => new(1,100,"fixture",Enumerable.Range(0,6).Select(i=>i<ids.Length?new Unit {SpeciesId=ids[i],Instance=i+50}:null).ToArray(),[],[]);
    private static BattleResult Match(RulesManifest rules,FrozenBoard own,FrozenBoard enemy)
    {
        var original=JsonSerializer.Serialize(enemy);var combat=new Combat(rules);var fast=combat.ResolveBattle(own,enemy,1,true);
        var slow=combat.ResolveBattle(own,enemy,1,true,true);Assert.Equal(slow.Trace,fast.Trace);
        Assert.Equal(slow.PlayerHp,fast.PlayerHp,7);Assert.Equal(slow.OpponentHp,fast.OpponentHp,7);Assert.Equal(original,JsonSerializer.Serialize(enemy));return fast;
    }
    [Theory]
    [InlineData("Has an additional +60 Damage for each Badge that you have.")]
    [InlineData("Your team has +100 HP. On Victory Increase this ability's HP bonus by 50.")]
    [InlineData("The next 2 items in your shop will be Berries. When you use an item, this gains +10 Damage.")]
    [InlineData("Give adjacent allies +1 Multicast permanently and transform them into random monsters of their rarity.")]
    [InlineData("Gain +7 Damage permanently. For this battle, transform into a random monster 1 rarity above your shop's highest available rarity.")]
    public void Grammars_reject_unknown_suffixes(string text)
    {Assert.NotEmpty(Effects(text));RuleCompiler.CompileText(text+" Unknown.",Trigger.Cast,out var understood);Assert.False(understood);}

    [Fact]
    public void Persistent_transform_revives_dead_targets_and_preserves_their_identity_and_state()
    {
        var actor=Species("actor",4,Effects("Give adjacent allies +1 Multicast permanently and transform them into random monsters of their rarity."),damage:0) with {ShopEligible=false};
        var victim=Species("victim",.5,Effects("Knockout self."),damage:0) with {ShopEligible=false};
        var final=Species("final",1,[]);var rules=RuleCompiler.Fixture() with {Species=[actor,victim,final]};var own=Board("actor","victim");
        own.Team[1]!.Level=2;own.Team[1]!.Shiny=true;own.Team[1]!.AddedTypes=["water"];own.Team[1]!.Counters["saved"]=7;
        var result=Match(rules,own,own with {MaxHp=5,Team=new Unit?[6]});var changed=result.PersistentTeam[1]!;
        Assert.Equal("final",changed.SpeciesId);Assert.Equal(51,changed.Instance);Assert.Equal(2,changed.Level);Assert.True(changed.Shiny);
        Assert.Equal(["water"],changed.AddedTypes);Assert.Equal(7,changed.Counters["saved"]);Assert.Equal(1,changed.Bonuses[7]);
        Assert.Contains(result.Trace,x=>x.StartsWith("5.00s side 0 slot 1: final"));
    }

    [Fact]
    public void Battle_transform_uses_final_shop_rarity_and_expires_while_permanent_damage_remains()
    {
        var actor=Species("actor",4,Effects("Gain +7 Damage permanently. For this battle, transform into a random monster 1 rarity above your shop's highest available rarity.",Trigger.BattleStart),2,1) with {ShopEligible=false};
        var common=Species("common",1,[]);var uncommon=Species("uncommon",1,[],1);var mythical=Species("mythical",1,[],5,99);
        var notes=ShopRules.Definition("trinkets","research-notes")!.Value;
        var trinket=new InventoryDefinition("research-notes","Notes",0,false,0,notes.Effects,"fixture",notes.Text);
        var rules=RuleCompiler.Fixture() with {Species=[actor,common,uncommon,mythical],Trinkets=[trinket]};
        var own=Board("actor") with {Bag=[new("research-notes",1,1)],Rank=1};
        var result=Match(rules,own,own with {MaxHp=100,Bag=[],Team=new Unit?[6]});
        Assert.Equal(1,result.Seconds);Assert.Equal(-6,result.OpponentHp);Assert.Equal("actor",result.PersistentTeam[0]!.SpeciesId);
        Assert.Equal(7,result.PersistentTeam[0]!.Bonuses[0]);Assert.Contains(result.Trace,x=>x.Contains("mythical"));
    }

    [Fact]
    public void Team_hp_growth_is_persistent_and_applied_once_at_the_next_battle()
    {
        var actor=Species("actor",1,Effects("Your team has +100 HP. On Victory Increase this ability's HP bonus by 50.",Trigger.Ongoing));
        var rules=RuleCompiler.Fixture() with {Species=[actor]};var own=Board("actor");
        var result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});Assert.Equal(200,result.PlayerHp);
        Assert.Equal(50,result.PersistentTeam[0]!.Counters["ability:team-hp"]);
        own=own with {Team=result.PersistentTeam};result=Match(rules,own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal(250,result.PlayerHp);Assert.Equal(100,result.PersistentTeam[0]!.Counters["ability:team-hp"]);
    }

    [Fact]
    public void Frozen_boards_capture_rank_foreign_pool_and_wins_without_aliasing_run_state()
    {
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(123,"fixture");
        env.State.Rank=7;env.State.ForeignPool=["foreign"];env.State.Wins=3;
        var board=FrozenBoard.From(env.State);env.State.Rank=8;env.State.ForeignPool[0]="changed";env.State.Wins=4;
        Assert.Equal(7,board.Rank);Assert.Equal(["foreign"],board.ForeignPool!);Assert.Equal(3,board.Wins);
        var actor=Species("actor",1,Effects("Has an additional +60 Damage for each Badge that you have.",Trigger.Ongoing),damage:0);
        rules=rules with {Species=[actor]};var own=Board("actor") with {Wins=3};
        var result=Match(rules,own,own with {MaxHp=179,Team=new Unit?[6]});Assert.Equal(-1,result.OpponentHp);
    }

    [Fact]
    public void Berry_credits_are_consumed_by_fresh_item_draws_and_tickets_do_not_spend_them()
    {
        var actor=Species("actor",1,Effects("The next 2 items in your shop will be Berries. When you use an item, this gains +10 Damage.",Trigger.Bought));
        var plain=Species("plain",1,[]);var berry=ItemRules.Definition("pom-berry")!.Value;var ticket=ItemRules.Definition("gray-ticket")!.Value;
        var rules=RuleCompiler.Fixture() with {Species=[actor,plain],Items=[new("pom-berry","Berry",0,false,0,berry.Effects,"fixture",berry.Text),
            new("gray-ticket","Ticket",0,false,0,ticket.Effects,"fixture",ticket.Text),new("other","Other",0,false,0,[],"fixture","")]};
        var env=new GameEnvironment(rules);env.Reset(123,"fixture");env.State.Shop[0]=new(new(){SpeciesId="actor",Instance=50},null,0);
        env.Step(new(Move.Buy,0,0));Assert.Equal(2,env.State.Counters["forced-berries"]);
        env.State.Shop[4]=new(null,"gray-ticket",0);env.Step(new(Move.BuyItem,4));Assert.Equal(2,env.State.Counters["forced-berries"]);
        Assert.Equal(10,env.State.Units[0]!.Bonuses[0]);env.State.ItemUses=1;
        env.Step(new(Move.Reroll));Assert.Equal("pom-berry",env.State.Shop[4]!.ItemId);Assert.Equal(1,env.State.Counters["forced-berries"]);
        env.GetObservation();env.GetLegalActions();Assert.Equal(1,env.State.Counters["forced-berries"]);
        env.Step(new(Move.BuyItem,4));Assert.Equal(20,env.State.Units[0]!.Bonuses[0]);
        env.Step(new(Move.Reroll));Assert.Equal(0,env.State.Counters["forced-berries"]);
    }

    [Fact]
    public void Merges_keep_the_highest_progress_and_earliest_acquisition_day()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(123,"fixture");
        env.State.Units[0]=new(){SpeciesId="striker",Instance=51,Counters=new(){["born-day"]=4,["progress"]=10}};
        env.State.Units[1]=new(){SpeciesId="striker",Instance=50,Counters=new(){["born-day"]=1,["progress"]=20}};
        env.State.Units[2]=new(){SpeciesId="striker",Instance=52,Counters=new(){["born-day"]=3,["progress"]=15}};
        env.Step(new(Move.Move,0,1));Assert.Equal(1,env.State.Units[0]!.Counters["born-day"]);Assert.Equal(20,env.State.Units[0]!.Counters["progress"]);
    }
}
