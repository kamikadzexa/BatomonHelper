using BatomonHelper.Simulation;
using System.Text.Json;
namespace BatomonHelper.Tests;

public sealed class CompleteRulesTests
{
    private static readonly Lazy<RulesManifest> Imported=new(()=>{
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null && !File.Exists(Path.Combine(root.FullName,"assets/simulation/rules.json")))root=root.Parent;
        return JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(root!.FullName,"assets/simulation/rules.json")))!;
    });

    [Fact]
    public void Gray_chip_is_free()
    {
        Assert.Equal(0,Imported.Value.Items.Single(item=>item.Id=="gray-chip").Cost);
    }
    public static IEnumerable<object[]> Trinkets=>Imported.Value.Trinkets.Select(x=>new object[]{x.Id});
    public static IEnumerable<object[]> Trainers=>Imported.Value.Trainers.Select(x=>new object[]{x.Id});
    private static void Play(GameEnvironment env)
    {
        for(var decisions=0;env.State.Phase!=Phase.Complete;decisions++){
            Assert.True(decisions<600,"Run failed to terminate.");var legal=env.GetLegalActions();Assert.NotEmpty(legal);
            var choice=legal.FirstOrDefault(x=>x.Kind==Move.Choose);
            if(choice is not null){env.Step(choice);continue;}
            if(env.State.Phase==Phase.Overflow){env.Step(legal.First(x=>x.Source==10));continue;}
            var purchase=legal.FirstOrDefault(x=>x.Kind==Move.Buy && env.State.Units.Count(x=>x is not null)<4);
            if(purchase is not null){env.Step(purchase);continue;}
            env.Step(new(Move.Finish),b=>b with {MaxHp=100,Team=new Unit?[6],Bag=[],ExtraTrainers=[],Trainer="youngster-f",Counters=[]});
        }
        Assert.InRange(env.State.Wins,0,10);
    }
    [Theory][MemberData(nameof(Trinkets))]
    public void Every_imported_trinket_is_acquired_and_executes_through_a_complete_run(string id)
    {
        var rules=Imported.Value;Assert.True(rules.Ready);var definition=rules.Trinkets.Single(x=>x.Id==id);
        var encounter=new EncounterDefinition("test-gift","Test",0,[new("Gift",null,[new(Trigger.None,Operation.GrantTrinket,Target.All,DefinitionId:id)])],"fixture");
        var env=new GameEnvironment(rules with {Encounters=(rules.Encounters??[]).Append(encounter).ToArray()});env.Reset(123,"youngster-f");
        env.OpenEncounter(encounter.Id);env.Step(new(Move.Choose,Choice:0));Play(env);
        if(AdvancedRules.Definition("trinkets",id) is { } advanced){Assert.True(AdvancedRules.TryCompile("trinkets",id,definition.Description,out _));Assert.False(AdvancedRules.TryCompile("trinkets",id,definition.Description+" Unknown.",out _));}
    }
    [Theory][MemberData(nameof(Trainers))]
    public void Every_imported_trainer_executes_initialization_and_complete_runs(string id)
    {var env=new GameEnvironment(Imported.Value);env.Reset(456,id);Play(env);}
    [Fact]
    public void Numeric_encoding_distinguishes_inventory_order_counters_payloads_and_all_choice_effects()
    {
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(1,"fixture");var encoding=new PolicyEncoding(rules);
        var first=encoding.Observation(env.GetObservation());env.State.Counters["progress-test"]=3;var second=encoding.Observation(env.GetObservation());Assert.False(first.SequenceEqual(second));
        env.State.Units[0]=new(){SpeciesId="striker",Devoured=new(){SpeciesId="guard",Level=3}};var third=encoding.Observation(env.GetObservation());Assert.False(second.SequenceEqual(third));
        var observation=env.GetObservation() with {Bag=[new("a",1,1),new("b",2,2)]};var ordered=encoding.Observation(observation);var reversed=encoding.Observation(observation with {Bag=observation.Bag.Reverse().ToArray()});Assert.False(ordered.SequenceEqual(reversed));
        Assert.DoesNotContain("RandomState",JsonSerializer.Serialize(observation));
        var choice=new Choice("Choice",null,[new(Trigger.None,Operation.Gold,Target.All,Amount:1),new(Trigger.None,Operation.Gold,Target.All,Amount:2)]);
        observation=observation with {Choices=[choice]};var action=encoding.Action(observation,new(Move.Choose,Choice:0));
        var changed=encoding.Action(observation with {Choices=[choice with {Effects=[choice.Effects[0],choice.Effects[1] with {Amount=3}]}]},new(Move.Choose,Choice:0));Assert.False(action.SequenceEqual(changed));
    }
    [Fact]
    public void Percentage_bonuses_add_and_new_same_tick_shock_does_not_increase_damage()
    {
        var basis=RuleCompiler.Fixture();var stats=new double[9];stats[0]=10;stats[5]=100;stats[7]=1;
        var attacker=basis.Species[0] with {Normal=[new(1,1,stats,[])]};
        var aura=new InventoryDefinition("aura","Aura",0,false,0,[new(Trigger.Ongoing,Operation.Multiply,Target.All,Amount:.2)],"fixture","");
        var rules=basis with {Species=[attacker],Trinkets=[aura]};
        var own=new FrozenBoard(1,100,"fixture",[new(){SpeciesId=attacker.Id},null,null,null,null,null],[new("aura",2)],[]);
        var enemy=own with {MaxHp=14,Team=new Unit?[6],Bag=[]};var result=new Combat(rules).ResolveBattle(own,enemy,1);
        Assert.Equal(1,result.Seconds);Assert.Equal(0,result.OpponentHp,7);
    }
}
