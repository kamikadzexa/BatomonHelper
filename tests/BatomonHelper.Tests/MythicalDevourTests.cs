using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class MythicalDevourTests
{
    private static Effect[] Effects(string text,Trigger trigger=Trigger.None)
    {var effects=RuleCompiler.CompileText(text,trigger,out var understood);Assert.True(understood);return effects;}
    private static Species Species(string id,double? cooldown,Effect[] effects,double damage=1) => RuleCompiler.Fixture().Species[0] with
    {Id=id,MergeGroup=id,Normal=Enumerable.Range(1,4).Select(l=>new LevelDefinition(l,cooldown,[damage,0,0,0,0,0,0,1,0],effects)).ToArray()};
    private static FrozenBoard Board(params string?[] ids) => new(1,100,"fixture",Enumerable.Range(0,6).Select(i=>i<ids.Length && ids[i] is { } id?new Unit {SpeciesId=id,Instance=i+1}:null).ToArray(),[],[]);
    private static BattleResult Match(RulesManifest rules,FrozenBoard own,FrozenBoard enemy)
    {
        var combat=new Combat(rules);var snapshot=JsonSerializer.Serialize(enemy);var fast=combat.ResolveBattle(own,enemy,1,true);
        var slow=combat.ResolveBattle(own,enemy,1,true,true);Assert.Equal(slow.Trace,fast.Trace);Assert.Equal(slow.PlayerHp,fast.PlayerHp,7);Assert.Equal(slow.OpponentHp,fast.OpponentHp,7);
        Assert.Equal(snapshot,JsonSerializer.Serialize(enemy));return fast;
    }
    [Theory]
    [InlineData("Mythical Item may appear in your shop. Ongoing Adjacent allies have +10% Damage per Mythical Item used.")]
    [InlineData("At the start of the next day, devour the ally in front and evolve into Rigalord.")]
    [InlineData("Spawn exact copies of the Batomon this devoured in empty slots the bottom row. They ONLY trigger when this casts.")]
    public void Grammar_rejects_unknown_suffixes(string text)
    {Assert.NotEmpty(Effects(text));RuleCompiler.CompileText(text+" Unknown.",Trigger.None,out var understood);Assert.False(understood);}

    [Fact]
    public void Mythical_shop_chance_stacks_from_team_and_bench_and_item_use_counts_drive_the_aura()
    {
        var fae=Species("fae",null,Effects("Mythical Item may appear in your shop. Ongoing Adjacent allies have +10% Damage per Mythical Item used."),0);
        var caster=Species("caster",1,[],10);var item=new InventoryDefinition("mythical","Mythical",5,false,0,[],"fixture","");
        var rules=RuleCompiler.Fixture() with {Species=[fae,caster],Items=[item]};var env=new GameEnvironment(rules);env.Reset(123,"fixture");
        for(var slot=0;slot<10;slot++)env.State.Units[slot]=new(){SpeciesId="fae",Instance=slot+100};
        env.State.Gold=100;env.Step(new(Move.Reroll));Assert.Equal("mythical",env.State.Shop[4]!.ItemId);
        env.State.ItemUses=0;env.Step(new(Move.Reroll));Assert.NotNull(env.State.Shop[4]!.Creature);
        var own=Board("fae","caster") with {Counters=new(){["items-used-rarity-5"]=2}};
        var result=Match(rules,own,own with {MaxHp=11,Team=new Unit?[6]});Assert.Equal(-1,result.OpponentHp);
    }

    [Fact]
    public void Devour_waits_for_next_day_keeps_an_exact_payload_and_removes_the_consumed_position()
    {
        var devour=Species("riglet",1,Effects("At the start of the next day, devour the ally in front and evolve into Rigalord."));
        var parent=Species("rigalord",1,Effects("Spawn exact copies of the Batomon this devoured in empty slots the bottom row. They ONLY trigger when this casts."));
        var victim=Species("victim",1,[]);var rules=RuleCompiler.Fixture() with {Species=[devour,parent,victim]};var env=new GameEnvironment(rules);env.Reset(123,"fixture");
        env.State.Units[4]=new(){SpeciesId="victim",Instance=50,Level=2,Shiny=true,AddedTypes=["water"],Bonuses=[7,0,0,0,0,0,0,0,0],Counters=new(){["saved"]=8}};
        env.State.Units[3]=new(){SpeciesId="riglet",Instance=51,Counters=new(){["born-day"]=1}};
        env.State.Choices=[new("Continue",null,[])];env.State.ActiveChoicePhase=env.State.Phase=Phase.Event;env.State.PendingShopStart=true;
        env.Step(new(Move.Choose,Choice:0));Assert.NotNull(env.State.Units[4]);Assert.Equal("riglet",env.State.Units[3]!.SpeciesId);
        env.Step(new(Move.Finish),b=>b with {MaxHp=1,Team=new Unit?[6]});Assert.Null(env.State.Units[4]);Assert.Equal("rigalord",env.State.Units[3]!.SpeciesId);
        var payload=env.State.Units[3]!.Devoured!;Assert.Equal(50,payload.Instance);Assert.Equal(2,payload.Level);Assert.True(payload.Shiny);
        Assert.Equal(7,payload.Bonuses[0]);Assert.Equal(8,payload.Counters["saved"]);
        var restored=new GameEnvironment(rules);restored.Restore(env.State);Assert.Equal(JsonSerializer.Serialize(env.State),JsonSerializer.Serialize(restored.State));
        env.State.Units[3]!.Devoured!.Bonuses[0]=99;Assert.Equal(7,restored.State.Units[3]!.Devoured!.Bonuses[0]);
    }

    [Fact]
    public void Summons_trigger_with_parent_casts_only_fill_empty_slots_and_expire_after_battle()
    {
        var parent=Species("parent",2,Effects("Spawn exact copies of the Batomon this devoured in empty slots the bottom row. They ONLY trigger when this casts."),0);
        var payload=Species("payload",.1,[],1);var blocker=Species("blocker",null,[],0);var trigger=Species("trigger",1,Effects("Trigger a random Rock ally. (Except other Opalion)",Trigger.Cast),0) with {Types=["fire"]};
        payload=payload with {Types=["rock"]};var rules=RuleCompiler.Fixture() with {Species=[parent,payload,blocker,trigger]};var own=Board("parent","trigger",null,"blocker");
        own.Team[0]!.Devoured=new(){SpeciesId="payload",Instance=50};var result=Match(rules,own,own with {MaxHp=3,Team=new Unit?[6]});
        Assert.Equal(4,result.Seconds);Assert.DoesNotContain(result.Trace,x=>x.Contains("payload") && (x.StartsWith("3.00s") || x.StartsWith("2.10s")));
        Assert.Equal(4,result.Trace.Count(x=>x.Contains("payload"))); // Two copies each cast at 2s and 4s.
        Assert.NotNull(result.PersistentTeam[3]);Assert.Null(result.PersistentTeam[4]);Assert.Null(result.PersistentTeam[5]);
        Assert.NotNull(result.PersistentTeam[0]!.Devoured);
    }

    [Fact]
    public void Summon_trigger_parentage_uses_positions_even_when_external_boards_omit_instance_ids()
    {
        var parent=Species("parent",2,Effects("Spawn exact copies of the Batomon this devoured in empty slots the bottom row. They ONLY trigger when this casts."),0);
        var payload=Species("payload",.1,[],1);var other=Species("other",1,[],0);var rules=RuleCompiler.Fixture() with {Species=[parent,payload,other]};
        var own=Board("parent","other");foreach(var unit in own.Team.OfType<Unit>())unit.Instance=0;
        own.Team[0]!.Devoured=new(){SpeciesId="payload"};var result=Match(rules,own,own with {MaxHp=4,Team=new Unit?[6]});
        Assert.DoesNotContain(result.Trace,x=>x.StartsWith("3.00s") && x.Contains("payload"));
        Assert.Equal(4,result.Seconds);
    }
}
