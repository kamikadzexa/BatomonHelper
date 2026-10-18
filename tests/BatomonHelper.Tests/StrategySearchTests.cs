using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class StrategySearchTests
{
    [Fact] public void Mechanics_guide_recognizes_self_buffs_driven_by_allied_status_events(){
        var basis=RuleCompiler.Fixture();var reactor=basis.Species[0] with {Normal=[basis.Species[0].Normal[0] with {Effects=[new(Trigger.AllyPoison,Operation.Add,Target.Self,Stat.Damage,2,EventAdjacent:true)]}]};
        var stats=(double[])basis.Species[1].Normal[0].Stats.Clone();stats[(int)Stat.Poison]=3;
        var producer=basis.Species[1] with {Normal=[basis.Species[1].Normal[0] with {Stats=stats}]};
        var rules=basis with {Species=[reactor,producer]};var guide=new StrategyKnowledgeGuide(rules);
        var a=new Unit(){SpeciesId=reactor.Id};var b=new Unit(){SpeciesId=producer.Id};
        Assert.True(guide.Compatibility(a,b,true)>0);Assert.Equal(0,guide.Compatibility(a,b,false));
    }
    [Fact] public void Search_is_legal_repeatable_and_cannot_read_or_mutate_live_rng(){
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(69,"fixture");var before=JsonSerializer.Serialize(env.State);
        var guide=new StrategyKnowledgeGuide(rules);var search=new StrategySearch(rules,guide);var opponents=IndependentOpponentArchive.Build(rules,101);
        SearchPrediction[] Predict(SearchEvaluation[] rows)=>rows.Select(r=>new SearchPrediction(Enumerable.Repeat(1d,r.Actions.Length).ToArray(),.5)).ToArray();
        var options=new SearchOptions(Seconds:10,Simulations:8,BatchSize:4,Seed:777,AnalysisSamples:1);
        var first=search.Plan(env.GetObservation(),opponents,options,Predict);
        Assert.Equal(before,JsonSerializer.Serialize(env.State));env.State.RandomState=999;
        var second=search.Plan(env.GetObservation(),opponents,options,Predict);
        Assert.Equal(first.Action,second.Action);Assert.Equal(first.Visits,second.Visits);
        Assert.Contains(first.Action,env.GetLegalActions());Assert.Equal(1,first.Visits.Sum(),8);Assert.Equal(8,first.Simulations);
    }
    [Fact] public void Finish_is_legal_at_decision_limit_and_cancel_is_observed(){
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(5,"fixture");var search=new StrategySearch(rules,new(rules));
        var pool=IndependentOpponentArchive.Build(rules,1);
        SearchPrediction[] Predict(SearchEvaluation[] rows)=>rows.Select(r=>new SearchPrediction(Enumerable.Repeat(1d,r.Actions.Length).ToArray(),.5)).ToArray();
        var plan=search.Plan(env.GetObservation(),pool,new(10,2,1,AnalysisSamples:1),Predict,shopDecisions:64);
        Assert.Equal(Move.Finish,plan.Action.Kind);
        Assert.Throws<OperationCanceledException>(()=>search.Plan(env.GetObservation(),pool,cancellation:new(true)));
    }
    [Fact] public void Wiki_pairs_affect_proposals_but_incompatible_balance_is_disabled(){
        var rules=RuleCompiler.Fixture();var ids=rules.Species.Take(2).Select(s=>s.Id).ToArray();
        var knowledge=new StrategyKnowledge(2,"fixture",rules.Balance,null,DateTimeOffset.UtcNow,[]) {Pairs=[new("pair",ids[0],ids[1],.1m,10,100,.6m,6,"fixture","today")]};
        var env=new GameEnvironment(rules);env.Reset(5,"fixture");env.State.Units[0]=new(){SpeciesId=ids[0]};
        Assert.Single(new StrategyKnowledgeGuide(rules,knowledge).Opportunities(env.GetObservation()));
        var incompatible=new StrategyKnowledgeGuide(rules,knowledge with {Balance="other"});
        Assert.Empty(incompatible.Opportunities(env.GetObservation()));Assert.Single(incompatible.Warnings);
    }
    [Fact] public void Opponent_adaptation_is_disabled_in_evaluation_and_user_session_share_is_capped(){
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(5,"fixture");var board=FrozenBoard.From(env.State);
        var a=new LeagueEntry(-1,0,"baseline",board);var b=new LeagueEntry(-1,1,"baseline",board);
        var user=new LeagueEntry(-2,0,"user",board,"one-session");
        var pool=new[]{a,b}.Concat(Enumerable.Repeat(user,100)).ToArray();var frozen=new StrategicOpponentPool(false);
        var before=Enumerable.Range(0,1000).Select(i=>frozen.Pick(pool,1,(ulong)i)).ToArray();
        for(var i=0;i<100;i++)frozen.Record(a,true);
        Assert.Equal(before,Enumerable.Range(0,1000).Select(i=>frozen.Pick(pool,1,(ulong)i)));
        Assert.InRange(before.Count(e=>e.Kind=="user"),60,140);
        var adaptive=new StrategicOpponentPool(true);for(var i=0;i<100;i++){adaptive.Record(a,true);adaptive.Record(b,false);}
        var chosen=Enumerable.Range(0,1000).Select(i=>adaptive.Pick(new[]{a,b},1,(ulong)i)).ToArray();
        Assert.True(chosen.Count(e=>e.Policy==1)>chosen.Count(e=>e.Policy==0));
    }
}
