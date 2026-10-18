using BatomonHelper.Simulation;
using BatomonHelper.Overlay;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class BoardAnalysisTests
{
    private static (RulesManifest,FrozenBoard) Board(double damage=10,double poison=0,Effect[]? effects=null){
        var basis=RuleCompiler.Fixture();var stats=new double[9];stats[0]=damage;stats[4]=poison;
        var species=basis.Species[0] with {Normal=[new(1,1,stats,effects??[])]};
        return (basis with {Species=[species]},new(1,300,"fixture",[new(){SpeciesId=species.Id,Instance=1},null,null,null,null,null],[],[]));
    }
    [Fact] public void Constant_damage_crosses_hp_without_stopping_curve_or_mutating_input(){
        var (rules,board)=Board();var before=JsonSerializer.Serialize(board);
        var result=new BoardAnalyzer(rules).Analyze(board,100,1);
        Assert.Equal(300,result.Checkpoints[^1].Damage,8);Assert.Equal(10,result.Checkpoints[^1].Dps,8);
        Assert.Equal(10,result.MedianKillSeconds);Assert.Equal(100d/3,result.MedianTimePercent!.Value,6);
        Assert.Equal(1,result.ReachProbability);Assert.Equal(before,JsonSerializer.Serialize(board));
    }
    [Fact] public void Empty_board_has_no_storm_damage_and_cannot_reach_target(){
        var (rules,board)=Board(0);var result=new BoardAnalyzer(rules).Analyze(board,100,1);
        Assert.All(result.Curve,p=>Assert.Equal(0,p.Damage));Assert.Null(result.MedianKillSeconds);Assert.Equal(0,result.ReachProbability);
    }
    [Fact] public void Poison_growth_uses_production_tick_order_and_matches_reference(){
        var (rules,board)=Board(0,2);var analyzer=new BoardAnalyzer(rules);
        var fast=analyzer.Analyze(board,100,1);var reference=analyzer.Analyze(board,100,1,reference:true);
        Assert.Equal(930,fast.Checkpoints[^1].Damage,8);Assert.Equal(60,fast.Checkpoints[^1].StatusStacks[1],8);
        Assert.Equal(fast.Checkpoints.Select(x=>x.Damage),reference.Checkpoints.Select(x=>x.Damage));
        Assert.Equal(10,fast.MedianKillSeconds);
    }
    [Fact] public void Reactive_stacks_change_effective_damage_and_expose_activation_counts(){
        var (rules,board)=Board(10,effects:[new(Trigger.Cast,Operation.Add,Target.Self,Stat.Damage,2)]);
        var result=new BoardAnalyzer(rules).Analyze(board,300,1);
        Assert.Equal(1230,result.Checkpoints[^1].Damage,8);Assert.Equal(70,result.Checkpoints[^1].Units[0].Stats[0],8);
        Assert.Equal(30,result.Checkpoints[^1].Units[0].Counters["effect:Cast/Add/Self/Damage"]);
    }
    [Fact] public void Multicast_and_existing_shock_are_not_double_counted(){
        var (rules,board)=Board();var stats=(double[])rules.Species[0].Normal[0].Stats.Clone();stats[5]=3;stats[7]=1;
        rules=rules with {Species=[rules.Species[0] with {Normal=[new(1,1,stats,[])]}]};
        var result=new BoardAnalyzer(rules).Analyze(board,300,2);
        Assert.Equal(result.Checkpoints[^1].Damage,result.Checkpoints[^1].DamageByKind.Values.Sum(),8);
        Assert.True(result.Checkpoints[^1].DamageByKind["Shock"]>0);
    }
    [Fact] public void Cancellation_and_invalid_inputs_fail_explicitly(){
        var (rules,board)=Board();var analyzer=new BoardAnalyzer(rules);
        Assert.Throws<ArgumentOutOfRangeException>(()=>analyzer.Analyze(board,0));
        Assert.Throws<OperationCanceledException>(()=>analyzer.Analyze(board,cancellation:new CancellationToken(true)));
    }
    [Fact] public void Measures_new_stacks_healing_and_shield_output_separately_from_damage(){
        var (rules,board)=Board();var stats=(double[])rules.Species[0].Normal[0].Stats.Clone();
        stats[1]=3;stats[2]=4;stats[3]=5;stats[4]=2;stats[5]=6;
        rules=rules with {Species=[rules.Species[0] with {Normal=[new(1,1,stats,[])]}]};
        var result=new BoardAnalyzer(rules).Analyze(board,100,1);var end=result.Checkpoints[^1];
        Assert.Equal(150,end.OutputTotals["Burn"]);Assert.Equal(60,end.OutputTotals["Poison"]);
        Assert.Equal(180,end.OutputTotals["Shock"]);Assert.Equal(90,end.OutputTotals["Heal"]);Assert.Equal(120,end.OutputTotals["Shield"]);
        Assert.Equal(5,end.OutputPerSecond["Burn"]);Assert.Equal(3,end.OutputPerSecond["Heal"]);Assert.Equal(4,end.OutputPerSecond["Shield"]);
        Assert.Contains(DamagePresentation.Rows(result),row=>row.Metric=="Electricity /s");
        Assert.Contains(DamagePresentation.Rows(result),row=>row.Metric=="Healing /s");
    }
    [Fact] public void Friendly_projection_shows_defeat_percentage_and_hides_unused_effects(){
        var (rules,board)=Board();var result=new BoardAnalyzer(rules).Analyze(board,100,1);
        var summary=DamagePresentation.Summary(result);
        Assert.Contains("33.3%",summary);Assert.Contains("HP dealt at",summary);
        Assert.Equal(new[]{"DPS"},DamagePresentation.Rows(result).Select(row=>row.Metric));
        var unreachable=new BoardAnalyzer(rules).Analyze(board,10000,1);
        Assert.Contains("HP dealt at >100%",DamagePresentation.Summary(unreachable));
    }
    [Fact] public void Slow_stacking_is_visible_between_casts_and_includes_multicast(){
        var (rules,board)=Board();var stats=(double[])rules.Species[0].Normal[0].Stats.Clone();
        stats[(int)Stat.Poison]=1;stats[(int)Stat.Shock]=1;stats[(int)Stat.Shield]=1;stats[(int)Stat.Multicast]=2;
        rules=rules with {Species=[rules.Species[0] with {Normal=[new(1,10,stats,[])]}]};
        var result=new BoardAnalyzer(rules).Analyze(board,100,1);
        foreach(var point in result.Checkpoints){
            Assert.Equal(.2,point.OutputPerSecond["Poison"],8);
            Assert.Equal(.2,point.OutputPerSecond["Shock"],8);
            Assert.Equal(.2,point.OutputPerSecond["Shield"],8);
        }
        var row=DamagePresentation.Rows(result).Single(value=>value.Metric=="Electricity /s");
        Assert.Equal("0.2",row.Start);Assert.Equal("0.2",row.End);
    }
    [Theory]
    [InlineData(.025,"0.025")]
    [InlineData(.001,"0.001")]
    [InlineData(.00001,"1E-5")]
    public void Small_positive_rates_never_round_to_zero(double value,string expected){
        Assert.Equal(expected,DamagePresentation.FormatRate(value));
    }
}
