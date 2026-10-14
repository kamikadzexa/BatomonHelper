using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class SimulationTests
{
    [Fact]
    public void Unknown_effects_block_training_instead_of_becoming_noops()
    {
        RuleCompiler.CompileText("Invent a new unknown effect.",Trigger.Cast,out var understood);
        Assert.False(understood);
        var rules=RuleCompiler.Fixture() with {Issues=[new("unknown","Ability","Missing handler","fixture")]};
        Assert.Throws<InvalidOperationException>(()=>new GameEnvironment(rules).Reset(1,"fixture"));
        var incompatible=RuleCompiler.Fixture() with {SimulationRevision=RulesManifest.CurrentRevision-1};
        Assert.False(incompatible.Ready);Assert.Throws<InvalidOperationException>(()=>new GameEnvironment(incompatible).Reset(1,"fixture"));
    }
    [Fact]
    public void Cooldown_charge_floor_and_status_shield_paths_are_separate()
    {
        Assert.Equal(3,Combat.EffectiveCooldown(6,1));
        Assert.Equal(.1,Combat.EffectiveCooldown(1,1000));
        double shield=10;
        Assert.Equal(7,Combat.Absorb(20,ref shield,true),8); Assert.Equal(0,shield);
        shield=10; Assert.Equal(10,Combat.Absorb(20,ref shield,false));
        Assert.Equal([0,2,4],Combat.Targets(1,Target.Adjacent));
        Assert.Equal(5,Combat.Targets(1,Target.Adjacent,true).Length);
    }
    [Fact]
    public void Negative_speed_is_preserved_and_maxed_evolutions_do_not_exclude_the_base_pool()
    {
        var basis=RuleCompiler.Fixture();var environment=new GameEnvironment(basis);environment.Reset(3,"fixture");
        var unit=new Unit {SpeciesId="striker"};unit.Bonuses[(int)Stat.Speed]=-.5;
        var own=new FrozenBoard(1,1000,"fixture",[unit,null,null,null,null,null],[],[]);
        var result=new Combat(basis).ResolveBattle(own,own,1,true);
        Assert.StartsWith("4.00s",result.Trace[0]);
        var evolved=basis.Species[1] with {Id="evolved",ShopEligible=false};
        var baseSpecies=basis.Species[0] with {EvolutionId="evolved",EvolutionLevel=3};
        var rules=basis with {Species=[baseSpecies,evolved,basis.Species[2]]};
        var run=new GameEnvironment(rules);run.Reset(5,"fixture");
        run.State.Units[0]=new() {SpeciesId="evolved",Level=3};
        run.State.Gold=100;
        run.Step(new(Move.Reroll));Assert.Contains(run.State.Shop,x=>x?.Creature?.SpeciesId=="striker");
        Assert.Equal(run.SellValue(new() {SpeciesId=baseSpecies.Id,Level=3}),run.SellValue(new() {SpeciesId=baseSpecies.Id,Level=4}));
    }
    [Fact]
    public void Diagnostic_complete_run_honors_loss_schedule_second_chance_and_terminal_reward()
    {
        var rules=RuleCompiler.Fixture();var environment=new GameEnvironment(rules);environment.Reset(2,"fixture");
        var expectedLives=new[]{9,8,6,4,1,1,0};
        foreach(var lives in expectedLives)
        {
            var step=environment.Step(new(Move.Finish),b=>b with {Team=[new(){SpeciesId="striker"},null,null,null,null,null]});
            Assert.Equal(lives,step.Observation.Lives);Assert.Equal(0,step.Reward);
        }
        Assert.True(environment.State.SecondChanceUsed);Assert.Equal(Phase.Complete,environment.State.Phase);Assert.Empty(environment.GetLegalActions());
        environment.Reset(3,"fixture");
        StepResult? result=null;
        for(var win=0;win<10;win++)result=environment.Step(new(Move.Finish),b=>b);
        Assert.True(result!.Terminal);Assert.Equal(1,result.Reward);Assert.Equal(10,environment.State.Wins);
    }
    [Fact]
    public void Seeded_restore_preserves_state_actions_and_future_shop_rolls()
    {
        var rules=RuleCompiler.Fixture();var a=new GameEnvironment(rules);a.Reset(123,"fixture");
        var bought=a.GetLegalActions().First(x=>x.Kind==Move.Buy);a.Step(bought);
        var b=new GameEnvironment(rules);b.Restore(a.State);
        Assert.Equal(a.GetLegalActions(),b.GetLegalActions());
        a.Step(new(Move.Reroll));b.Step(new(Move.Reroll));
        Assert.Equal(JsonSerializer.Serialize(a.State),JsonSerializer.Serialize(b.State));
        var original=a.State.Clone();Assert.Throws<ArgumentException>(()=>a.Step(new(Move.Buy,99,0)));
        Assert.Equal(JsonSerializer.Serialize(original),JsonSerializer.Serialize(a.State));
    }
    [Fact]
    public void Simultaneous_heal_adds_to_negative_hp_and_double_defeat_is_player_win()
    {
        var basis=RuleCompiler.Fixture();
        var stats=new double[9];stats[0]=307;stats[1]=30;stats[7]=1;
        var species=basis.Species[0] with {Normal=[new(1,1,stats,[])]};
        var rules=basis with {Species=[species]};
        var board=new FrozenBoard(1,300,"fixture",[new() {SpeciesId=species.Id},null,null,null,null,null],[],[]);
        var result=new Combat(rules).ResolveBattle(board,board,1,true);
        Assert.Contains("1.00s",result.Trace[0]);Assert.True(result.BothDefeated);Assert.True(result.PlayerWon);
        Assert.Equal(2,result.Seconds,8); // First tick leaves 23 HP, then the second defeats both sides.
    }
    [Fact]
    public void Self_knockout_does_not_erase_the_cast_and_inventory_rewards_fire_once()
    {
        var basis=RuleCompiler.Fixture();var stats=new double[9];stats[0]=100;stats[7]=1;
        var species=basis.Species[0] with {Normal=[new(1,1,stats,[new(Trigger.Cast,Operation.Knockout,Target.Self)])]};
        var trinket=new InventoryDefinition("reward","Reward",0,false,0,[new(Trigger.Victory,Operation.Gold,Target.All,Amount:8)],"fixture","");
        var rules=basis with {Species=[species],Trinkets=[trinket]};
        var own=new FrozenBoard(1,50,"fixture",[new(){SpeciesId=species.Id},null,null,null,null,null],[new("reward",1)],[]);
        var enemy=own with {Bag=[]};var result=new Combat(rules).ResolveBattle(own,enemy,1);
        Assert.True(result.PlayerWon);Assert.Equal(1,result.Seconds);Assert.Equal(8,result.Gold);
    }
    [Fact]
    public void Ongoing_flat_inventory_hp_is_applied_once_per_copy()
    {
        var basis=RuleCompiler.Fixture();var trinket=new InventoryDefinition("hp","HP",0,false,0,[new(Trigger.Ongoing,Operation.TeamHp,Target.All,Amount:50)],"fixture","");
        var rules=basis with {Trinkets=[trinket]};
        var own=new FrozenBoard(1,100,"fixture",[null,null,null,null,null,null],[new("hp",1)],[]);
        var result=new Combat(rules).ResolveBattle(own,own with {Bag=[]},1);
        Assert.True(result.PlayerWon);Assert.False(result.BothDefeated);Assert.True(result.PlayerHp>0);
    }
    [Fact]
    public void Optimized_and_reference_battles_have_identical_ordered_traces()
    {
        var rules=RuleCompiler.Fixture();var environment=new GameEnvironment(rules);environment.Reset(2,"fixture");
        environment.Step(environment.GetLegalActions().First(x=>x.Kind==Move.Buy));
        var board=FrozenBoard.From(environment.State);var combat=new Combat(rules);
        for(ulong seed=0;seed<10;seed++)
        {
            var fast=combat.ResolveBattle(board,board,seed,true);var reference=combat.ResolveBattle(board,board,seed,true,true);
            Assert.Equal(reference.Trace,fast.Trace);Assert.Equal(reference.PlayerHp,fast.PlayerHp);Assert.Equal(reference.Seconds,fast.Seconds);
        }
    }
    [Fact]
    public void Opponents_are_frozen_and_observations_never_expose_rng()
    {
        var environment=new GameEnvironment(RuleCompiler.Fixture());environment.Reset(99,"fixture");environment.Step(environment.GetLegalActions().First(x=>x.Kind==Move.Buy));
        var board=FrozenBoard.From(environment.State);var saved=JsonSerializer.Serialize(board);
        environment.State.Units[0]!.Bonuses[0]=10000;
        Assert.Equal(saved,JsonSerializer.Serialize(board));Assert.DoesNotContain("RandomState",JsonSerializer.Serialize(environment.GetObservation()));
        var encoding=new PolicyEncoding(RuleCompiler.Fixture());var observation=environment.GetObservation();
        Assert.True(encoding.Observation(observation).Length>encoding.ObservationWidth);
        foreach(var action in environment.GetLegalActions())Assert.Equal(PolicyEncoding.ActionWidth,encoding.Action(observation,action).Length);
    }
    [Fact]
    public async Task History_freezes_boards_reloads_filters_and_preserves_pinned_runs()
    {
        var path=Path.Combine(Path.GetTempPath(),"batomon-history-"+Guid.NewGuid().ToString("N"));
        var rules=RuleCompiler.Fixture();var environment=new GameEnvironment(rules);environment.Reset(2,"fixture");environment.Step(environment.GetLegalActions().First(x=>x.Kind==Move.Buy));
        var observation=environment.GetObservation();var board=FrozenBoard.From(environment.State);var result=new Combat(rules).ResolveBattle(board,board,1);
        var day=new DayRecord(1,observation,board,result,1,10,[]);
        await using(var history=new HistoryStore(path,recent:1,best:1,generations:1))
        {
            var record=new RunRecord(new("pinned",0,0,"fixture",1,1,1,false,false,DateTimeOffset.UtcNow,rules.Hash),[day]);
            await history.AppendAsync(record);observation.Units[0]!.Bonuses[0]=9000;await history.FlushAsync();history.Pin("pinned",true);
            Assert.False(history.TogglePin("pinned"));Assert.True(history.TogglePin("pinned"));
            Assert.Equal(0,(await history.LoadAsync("pinned")).Days[0].BeforeBattle.Units[0]!.Bonuses[0]);
            await history.AppendAsync(record with {Summary=record.Summary with {Wins=9}});await history.FlushAsync();
            Assert.Equal(1,(await history.LoadAsync("pinned")).Summary.Wins);
            Assert.Equal(0,(await history.LoadAsync("pinned")).Days[0].BeforeBattle.Units[0]!.Bonuses[0]);
            for(var i=1;i<5;i++)await history.AppendAsync(new(record.Summary with {RunId="run-"+i,Generation=i,Wins=i,Evaluation=true,CompletedAt=DateTimeOffset.UtcNow.AddSeconds(i)},[day]));
            await history.AppendAsync(new(record.Summary with {RunId="training-4",Generation=4,Wins=3,Evaluation=false,CompletedAt=DateTimeOffset.UtcNow.AddSeconds(5)},[day]));
            await history.FlushAsync();Assert.Contains(history.List(),x=>x.RunId=="pinned");Assert.Single(history.List(evaluation:true));
            Assert.Contains(history.List(evaluation:false),x=>x.RunId=="training-4");
            Assert.Throws<ArgumentException>(()=>history.Pin("../escape",true));
        }
        await using var reopened=new HistoryStore(path,recent:1,best:1,generations:1);
        Assert.Equal("run-4",reopened.List(best:true).First().RunId);Assert.True(reopened.List().Single(x=>x.RunId=="pinned").Pinned);
    }
}
