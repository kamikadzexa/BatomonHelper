using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ImportedCreatureCoverageTests
{
    [Fact]
    public void Every_imported_normal_and_shiny_level_has_a_handler_and_executes_in_an_isolated_fixture()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"assets","simulation","rules.json")))root=root.Parent;
        Assert.NotNull(root);
        var imported=JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(root.FullName,"assets","simulation","rules.json")))!;
        Assert.DoesNotContain(imported.Issues,x=>x.Kind is "Ability" or "Variant" or "Creature");
        var basis=RuleCompiler.Fixture();var support=basis.Species[0] with {Id="coverage-support",MergeGroup="coverage-support",Types=["fire","water","grass","rock","bug","flying","electric","toxic","dragon","fighting"],
            Normal=Enumerable.Range(1,4).Select(l=>new LevelDefinition(l,2,[1,0,0,0,0,0,0,1,0],[])).ToArray()};
        // Inventory effects are isolated here; dedicated inventory tests verify those handlers.
        // This explicit fixture never certifies a complete game ruleset or game performance.
        var species=imported.Species.Select(x=>x with {Normal=x.Normal.Select(l=>l with {Cooldown=.1}).ToArray(),Shiny=x.Shiny.Select(l=>l with {Cooldown=.1}).ToArray()}).Append(support).ToArray();
        var rules=basis with {Hash="imported-creature-coverage",Species=species,Trinkets=imported.Trinkets.Select(x=>x with {Effects=[]}).ToArray()};
        var count=0;
        foreach(var creature in imported.Species)
        foreach(var (levels,shiny) in new[] {(creature.Normal,false),(creature.Shiny,true)})
        foreach(var level in levels)
        {
            var actor=new Unit {SpeciesId=creature.Id,Instance=2,Level=level.Level,Shiny=shiny};
            var own=new FrozenBoard(1,100,"fixture",[new(){SpeciesId=support.Id,Instance=1,Level=3},null,null,actor,null,null],[],[],Rank:14);
            var enemy=own with {MaxHp=1,Team=new Unit?[6]};var combat=new Combat(rules);
            try
            {
                var fast=combat.ResolveBattle(own,enemy,123,true);var reference=combat.ResolveBattle(own,enemy,123,true,true);
                Assert.Equal(reference.Trace,fast.Trace);Assert.Equal(reference.PlayerHp,fast.PlayerHp,7);Assert.Equal(reference.OpponentHp,fast.OpponentHp,7);
                Assert.True(double.IsFinite(fast.Seconds));count++;
            }
            catch(Exception e){throw new InvalidOperationException($"Creature coverage failed: {creature.Id}/{(shiny?"shiny":"normal")}/{level.Level}",e);}
        }
        Assert.Equal(imported.Species.Sum(x=>x.Normal.Length+x.Shiny.Length),count);Assert.True(count>1000);
    }
}
