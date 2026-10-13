using BatomonHelper.Simulation;
using System.Text.Json;
namespace BatomonHelper.Tests;
public sealed class ConsumableTrainerTests
{
    [Fact]
    public void Enough_copies_inside_one_stack_promote_without_another_matching_creature()
    {
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(7,"fixture");
        env.State.Units[0]=new(){SpeciesId="striker",Instance=20,Copies=2};
        env.State.Units[1]=new(){SpeciesId="striker",Instance=21,Copies=2};
        env.Step(new(Move.Merge,1,0));Assert.Equal(2,env.State.Units[0]!.Level);Assert.Equal(2,env.State.Units[0]!.Copies);
        var purchase=env.GetLegalActions().First(x=>x.Kind==Move.Buy);env.Step(purchase);
        Assert.Equal(3,env.State.Units[0]!.Level);Assert.Equal(1,env.State.Units[0]!.Copies);
    }
    private static RulesManifest Rules(params string[] ids)
    {
        var basis=RuleCompiler.Fixture();var trainers=basis.Trainers.Concat(ids.Where(x=>AdvancedRules.Definition("trainers",x) is not null).Select(x=>{
            var d=AdvancedRules.Definition("trainers",x)!.Value;return new InventoryDefinition(x,x,0,false,0,d.Effects,"fixture",d.Text);})).ToArray();
        var trinkets=ids.Where(x=>AdvancedRules.Definition("trinkets",x) is not null).Select(x=>{var d=AdvancedRules.Definition("trinkets",x)!.Value;return new InventoryDefinition(x,x,3,false,0,d.Effects,"fixture",d.Text);})
            .Concat(Enumerable.Range(0,6).Select(x=>new InventoryDefinition("junk-"+x,"Junk",x,false,0,[],"fixture",""))).ToArray();
        var species=Enumerable.Range(0,6).Select(x=>basis.Species[0] with {Id="unit-"+x,MergeGroup="unit-"+x,Rarity=x,Types=["grass"],Cost=10,Shiny=basis.Species[0].Normal}).ToArray();
        return basis with {Species=species,Trainers=trainers,Trinkets=trinkets};
    }
    private static void Grant(GameEnvironment env,RulesManifest rules,string id)
    {
        env.OpenEncounter("grant-"+id);env.Step(new(Move.Choose,Choice:0));
    }
    private static RulesManifest Grants(RulesManifest rules,params string[] ids)=>rules with {Encounters=ids.Select(id=>new EncounterDefinition("grant-"+id,"Grant",0,
        [new("Grant",null,[new(Trigger.None,Operation.GrantTrinket,Target.All,DefinitionId:id)])],"fixture")).Append(new("gift","Gift",0,[new("Gift",null,[new(Trigger.None,Operation.TrinketGift,Target.All,Rarity:0)])],"fixture")).ToArray()};
    [Fact]
    public void Duplicators_apply_only_to_gifts_and_consume_when_a_nonunique_reward_is_selected()
    {
        var rules=Grants(Rules("mini-duplicator","mega-duplicator","ultra-duplicator"),"mini-duplicator","mega-duplicator","ultra-duplicator","junk-0");
        var env=new GameEnvironment(rules);env.Reset(1,"fixture");foreach(var id in new[]{"mini-duplicator","mega-duplicator","ultra-duplicator"})Grant(env,rules,id);
        Grant(env,rules,"junk-0");Assert.Equal(1,env.State.Bag.Single(x=>x.Id=="junk-0").Quantity);
        env.OpenEncounter("gift");env.Step(new(Move.Choose,Choice:0));env.Step(new(Move.Choose,Choice:0));
        Assert.Equal(6,env.State.Bag.Where(x=>x.Id=="junk-0").Sum(x=>x.Quantity));
        Assert.DoesNotContain(env.State.Bag,x=>x.Id is "mini-duplicator" or "mega-duplicator");Assert.Contains(env.State.Bag,x=>x.Id=="ultra-duplicator");
    }
    [Fact]
    public void Take_all_and_gift_tier_upgrade_survive_checkpoint_before_selection()
    {
        var rules=Grants(Rules("greedy-gloves","treasure-map"),"greedy-gloves","treasure-map");var env=new GameEnvironment(rules);env.Reset(2,"fixture");
        Grant(env,rules,"greedy-gloves");Grant(env,rules,"treasure-map");env.OpenEncounter("gift");env.Step(new(Move.Choose,Choice:0));
        Assert.True(env.State.TakeAllChoices);Assert.Equal(3,env.State.Choices.Length);Assert.All(env.State.Choices,c=>Assert.Equal("junk-1",c.DefinitionId));
        var restored=new GameEnvironment(rules);restored.Restore(env.State);var action=new SimAction(Move.Choose,Choice:0);env.Step(action);restored.Step(action);
        Assert.Equal(3,env.State.Bag.Where(x=>x.Id=="junk-1").Sum(x=>x.Quantity));Assert.Equal(JsonSerializer.Serialize(env.State),JsonSerializer.Serialize(restored.State));
        Assert.DoesNotContain(env.State.Bag,x=>x.Id is "greedy-gloves" or "treasure-map");
    }
    [Fact]
    public void Incense_limits_five_fresh_shops_and_one_shot_purchase_effects_preserve_identity()
    {
        var rules=Grants(Rules("blue-incense","speed-whistle","power-band","upgrade-disc"),"blue-incense","speed-whistle","power-band","upgrade-disc");
        var env=new GameEnvironment(rules);env.Reset(3,"fixture");Grant(env,rules,"blue-incense");env.State.Gold=1000;
        for(var roll=0;roll<5;roll++){env.Step(new(Move.Reroll));Assert.All(env.State.Shop.Where(x=>x?.Creature is not null),x=>Assert.Equal("unit-2",x!.Creature!.SpeciesId));}
        Assert.DoesNotContain(env.State.Bag,x=>x.Id=="blue-incense");
        foreach(var id in new[]{"speed-whistle","power-band","upgrade-disc"})Grant(env,rules,id);
        var buy=env.GetLegalActions().First(x=>x.Kind==Move.Buy);var instance=env.State.Shop[buy.Source]!.Creature!.Instance;env.Step(buy);
        var unit=env.State.Units.OfType<Unit>().Single(x=>x.Instance==instance);Assert.Equal(2,unit.Level);Assert.Equal(.12,unit.Bonuses[6]);Assert.Equal(20,unit.Bonuses[0]);
        Assert.DoesNotContain(env.State.Bag,x=>x.Id is "speed-whistle" or "upgrade-disc");
    }
    [Fact]
    public void Twins_clone_merge_result_with_zero_resale_and_chemist_growth_is_persistent()
    {
        var rules=Rules("twins","chemist");var env=new GameEnvironment(rules);env.Reset(4,"twins");env.State.ExtraTrainers=["chemist"];
        env.State.Units[0]=new(){SpeciesId="unit-0",Instance=10,Level=2};env.State.Units[1]=new(){SpeciesId="unit-0",Instance=11,Level=2};
        env.Step(new(Move.Merge,1,0));var copy=env.State.Units.OfType<Unit>().Single(x=>x.Instance!=10);
        Assert.Equal(3,copy.Level);Assert.Equal(0,env.SellValue(copy));Assert.Equal(1,env.State.Counters["chemist-levels"]);
        Assert.Equal(env.State.Units[0]!.Bonuses,copy.Bonuses);Assert.NotEqual(env.State.Units[0]!.Instance,copy.Instance);
    }
}
