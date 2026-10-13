using BatomonHelper.Simulation;

namespace BatomonHelper.Tests;

public sealed class TrainingObjectiveTests
{
    [Fact]
    public void Reversible_moves_and_purchase_sale_cycles_cannot_farm_reward()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(3,"fixture");
        var buy=env.GetLegalActions().First(x=>x.Kind==Move.Buy && x.Target==0);
        var before=TrainingObjective.DeploymentPotential(env.State.Units);var bought=env.Step(buy);var purchaseReward=TrainingObjective.Reward(before,env.State,bought);
        before=TrainingObjective.DeploymentPotential(env.State.Units);var outStep=env.Step(new(Move.Move,0,6));var outReward=TrainingObjective.Reward(before,env.State,outStep);
        before=TrainingObjective.DeploymentPotential(env.State.Units);var backStep=env.Step(new(Move.Move,6,0));var backReward=TrainingObjective.Reward(before,env.State,backStep);
        Assert.Equal(-.004,outReward+backReward,10);
        before=TrainingObjective.DeploymentPotential(env.State.Units);var sold=env.Step(new(Move.Sell,0));var sellReward=TrainingObjective.Reward(before,env.State,sold);
        Assert.Equal(-.004,purchaseReward+sellReward,10);
    }

    [Fact]
    public void Shop_limit_publishes_finish_and_keeps_required_choices_legal()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(3,"fixture");
        Assert.True(TrainingObjective.Candidates(env,63,64).Length>1);
        Assert.Equal(new SimAction(Move.Finish),Assert.Single(TrainingObjective.Candidates(env,64,64)));
        env.State.Phase=Phase.Event;env.State.Choices=[new("Continue",null,[])];
        Assert.Equal(new SimAction(Move.Choose,Choice:0),Assert.Single(TrainingObjective.Candidates(env,100,64)));
        env.State.Phase=Phase.Overflow;env.State.Overflow=new() {SpeciesId="striker"};
        env.State.Units[0]=new() {SpeciesId="striker"};
        Assert.All(TrainingObjective.Candidates(env,100,64),x=>Assert.Equal(10,x.Source));
    }

    [Fact]
    public void Battle_feedback_is_immediate_and_penalizes_unused_owned_units()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(3,"fixture");
        var purchase=env.GetLegalActions().First(x=>x.Kind==Move.Buy && x.Target==6);env.Step(purchase);
        var before=TrainingObjective.DeploymentPotential(env.State.Units);
        var step=env.Step(new(Move.Finish),b=>b);
        Assert.Equal(0,step.Reward);Assert.False(step.Terminal);
        Assert.Equal(.1-.002-.10,TrainingObjective.Reward(before,env.State,step),10);
    }

    [Fact]
    public void Deployment_features_make_the_bench_fix_visible_without_hiding_finish()
    {
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(3,"fixture");
        env.Step(env.GetLegalActions().First(x=>x.Kind==Move.Buy&&x.Target==6));
        var finish=env.GetLegalActions().Single(x=>x.Kind==Move.Finish);
        var deploy=env.GetLegalActions().Single(x=>x.Kind==Move.Move&&x.Source==6&&x.Target==0);
        var encoding=new PolicyEncoding(rules);var observation=env.GetObservation();
        var deployFeatures=encoding.Action(observation,deploy);var finishFeatures=encoding.Action(observation,finish);
        Assert.Equal(1,deployFeatures[37]);Assert.Equal(-1,deployFeatures[38]);Assert.Equal(1,finishFeatures[39]);
        var before=TrainingObjective.DeploymentPotential(env.State.Units);var step=env.Step(deploy);
        Assert.Equal(.118,TrainingObjective.Reward(before,env.State,step),10);
    }

    [Fact]
    public void Occupied_slot_swaps_have_one_canonical_candidate()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(3,"fixture");
        env.State.Units[0]=new(){SpeciesId="striker",Instance=100};env.State.Units[1]=new(){SpeciesId="guard",Instance=101};
        var swaps=env.GetLegalActions().Where(x=>x.Kind==Move.Move&&new[]{x.Source,x.Target}.Order().SequenceEqual(new[]{0,1})).ToArray();
        Assert.Equal(new SimAction(Move.Move,0,1),Assert.Single(swaps));
        env.Step(swaps[0]);Assert.Equal("guard",env.State.Units[0]!.SpeciesId);Assert.Equal("striker",env.State.Units[1]!.SpeciesId);
    }

    [Fact]
    public void Evaluation_opponent_schedule_is_keyed_only_by_common_run_coordinates()
    {
        var seed=TrainingObjective.EvaluationOpponentSeed(69,2,17,5);
        Assert.Equal(seed,TrainingObjective.EvaluationOpponentSeed(69,2,17,5));
        Assert.NotEqual(seed,TrainingObjective.EvaluationOpponentSeed(69,2,18,5));
        Assert.NotEqual(seed,TrainingObjective.EvaluationOpponentSeed(69,3,17,5));
    }
}
