using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class TrainingV4Tests
{
    [Fact]
    public void Owned_state_clone_copies_every_property_without_sharing_mutable_storage()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(69,"fixture");
        env.State.Units[0]=new(){SpeciesId="striker",Instance=77,Devoured=new(){SpeciesId="guard"},Counters=new(){["counter"]=2}};
        env.State.PendingChoices=[new(Phase.Event,[new("Choose",null,[new(Trigger.None,Operation.Gold,Target.All,Amount:4)])])];
        var clone=env.State.Clone();Assert.Equal(JsonSerializer.Serialize(env.State),JsonSerializer.Serialize(clone));
        foreach(var property in typeof(RunState).GetProperties()){
            var original=property.GetValue(env.State);var copied=property.GetValue(clone);
            if(original is Array array&&array.Length>0||original is Dictionary<string,double>)Assert.NotSame(original,copied);
        }
        clone.Units[0]!.Devoured!.Bonuses[0]=123;clone.Units[0]!.Counters["counter"]=90;
        clone.PendingChoices[0].Choices[0].Effects[0]=new(Trigger.None,Operation.Gold,Target.All,Amount:100);
        Assert.Equal(0,env.State.Units[0]!.Devoured!.Bonuses[0]);Assert.Equal(2,env.State.Units[0]!.Counters["counter"]);
        Assert.Equal(4,env.State.PendingChoices[0].Choices[0].Effects[0].Amount);
    }
    [Fact]
    public void Published_actions_are_owned_and_invalidated_by_step_and_restore()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(4,"fixture");
        var publication=env.PublishActions(x=>x);var state=env.State.Clone();
        var index=publication.Actions.ToList().FindIndex(x=>x.Kind==Move.Buy);env.StepPublished(publication,index);
        Assert.Throws<ArgumentException>(()=>env.StepPublished(publication,index));
        publication=env.PublishActions(x=>x);env.Restore(state);
        Assert.Throws<ArgumentException>(()=>env.StepPublished(publication,0));
        Assert.Throws<ArgumentException>(()=>env.PublishActions(_=>[new(Move.Buy,999)]));
        Assert.Throws<ArgumentException>(()=>env.Step(new(Move.Buy,999)));
    }
    [Fact]
    public void Shop_memory_blocks_reversible_cycles_but_resets_after_real_changes()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(69,"fixture");var memory=new ShopTurnMemory();
        var buy=env.GetLegalActions().First(x=>x.Kind==Move.Buy&&x.Target==0);env.Step(buy);memory.Applied(buy,env.State,false);
        var legal=memory.Filter(env.State,env.GetLegalActions());var move=legal.First(x=>x.Kind==Move.Move&&x.Source==0&&x.Target==6);
        env.Step(move);memory.Applied(move,env.State,false);
        Assert.DoesNotContain(memory.Filter(env.State,env.GetLegalActions()),x=>x.Kind==Move.Move&&x.Source==6&&x.Target==0);
        var snapshot=memory.Visited;var resumed=new ShopTurnMemory();resumed.Restore(snapshot,memory.LastAction,memory.RepeatedKind);
        Assert.Equal(memory.Filter(env.State,env.GetLegalActions()),resumed.Filter(env.State,env.GetLegalActions()));
        var sell=new SimAction(Move.Sell,6);env.Step(sell);memory.Applied(sell,env.State,false);
        env.State.Phase=Phase.Event;env.State.Choices=[new("Continue",null,[])];
        Assert.Single(memory.Filter(env.State,env.GetLegalActions()));
    }
    [Fact]
    public void Overflow_candidates_only_move_the_waiting_unit_and_keep_sales_and_merges()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(69,"fixture");
        env.State.Units[0]=new(){SpeciesId="striker",Instance=11};env.State.Units[1]=new(){SpeciesId="guard",Instance=12};
        env.State.Overflow=new(){SpeciesId="striker",Instance=13};env.State.Phase=Phase.Overflow;
        var native=TrainingObjective.Candidates(env,0,64);
        Assert.All(native.Where(x=>x.Kind==Move.Move),x=>Assert.Equal(10,x.Source));
        Assert.Contains(native,x=>x.Kind==Move.Sell&&x.Source==10);Assert.Contains(native,x=>x.Kind==Move.Sell&&x.Source==0);
        var legacy=TrainingObjective.Candidates(Phase.Overflow,env.GetLegalActions(),0,64,false);
        Assert.Contains(legacy,x=>x.Kind==Move.Move&&x.Source==0&&x.Target==1);
    }
    [Fact]
    public void League_categories_and_retention_are_explicit_and_day_matched()
    {
        var env=new GameEnvironment(RuleCompiler.Fixture());env.Reset(69,"fixture");var board=FrozenBoard.From(env.State);
        var league=new OpponentLeague();league.SetGeneration(10);
        for(var i=0;i<100;i++)league.Add(new(10,0,"current",board with {Wins=i%3}));
        Assert.True(league.Count<=6);
        league.Add(new(9,0,"current",board));league.Add(new(10,1,"champion",board));league.Add(new(-1,0,"baseline",board));league.Add(new(-2,0,"user",board,"mine"));
        Assert.All(league.Candidates(1,"current"),x=>Assert.Equal("current",x.Kind));
        Assert.All(league.Candidates(1,"recent"),x=>Assert.Equal(9,x.Generation));
        var saved=league.Entries;var restored=new OpponentLeague();restored.Restore(saved,10);
        for(ulong seed=0;seed<100;seed++)Assert.Equal(league.Pick(1,seed),restored.Pick(1,seed));
        Assert.Throws<InvalidOperationException>(()=>league.Pick(2,1,true));
    }
    [Fact]
    public void Targeted_items_require_an_eligible_table_or_bench_creature()
    {
        var basis=RuleCompiler.Fixture();
        var heal=new InventoryDefinition("water-heal","Water heal",0,false,1,
            [new(Trigger.Bought,Operation.Add,Target.All,Stat.Heal,10,Type:"water")],"fixture","Give Water monsters +10 Heal.");
        var rules=basis with {Items=[heal]};var env=new GameEnvironment(rules);env.Reset(3,"fixture");
        Assert.Null(env.State.Shop[4]!.ItemId);
        env.State.Units[6]=new(){SpeciesId="striker",AddedTypes=["water"]};env.Step(new(Move.Reroll));
        Assert.Equal("water-heal",env.State.Shop[4]!.ItemId);
    }
    [Fact]
    public void Turn_cap_ends_runs_after_the_last_turn_even_when_the_target_is_not_met()
    {
        var basis=RuleCompiler.Fixture();var rules=basis with {Parameters=basis.Parameters with {TurnCap=2}};
        var env=new GameEnvironment(rules);env.Reset(9,"fixture");
        env.State.Day=2;env.State.Phase=Phase.Shop;
        var result=env.Step(new(Move.Finish),b=>b);Assert.True(result.Terminal);Assert.Equal(Phase.Complete,env.State.Phase);
        Assert.Equal(0,env.GetObservation().TurnsRemaining);
    }
    [Fact]
    public void User_session_replays_resumes_deduplicates_and_rejects_tampering()
    {
        var rules=RuleCompiler.Fixture();var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try{var store=new UserSessionStore(directory,rules);var play=new PlaySession(rules,69,"fixture");
            play.Apply(play.Environment.GetLegalActions().First(x=>x.Kind==Move.Buy));play.Apply(new(Move.Finish));
            play.SetPool(true);store.Save(play.Session);store.Validate(play.Session);
            var resumed=new PlaySession(rules,store.List().Single(),store);
            Assert.Equal(JsonSerializer.Serialize(play.Environment.State),JsonSerializer.Serialize(resumed.Environment.State));
            store.Save(play.Session with {Id="copy"});Assert.Single(store.Opponents());
            store.Save(play.Session with {Enabled=false});Assert.Single(store.Opponents());
            store.Delete("copy");Assert.Empty(store.Opponents());
            var tampered=play.Session.Current.Clone();tampered.Gold=999;Assert.Throws<InvalidDataException>(()=>store.Validate(play.Session with {Current=tampered}));
        }finally{var full=Path.GetFullPath(directory);var tempRoot=Path.GetFullPath(Path.GetTempPath());
            if(!full.StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full),"^[a-f0-9]{32}$"))throw new InvalidOperationException("Unsafe test cleanup path.");
            if(Directory.Exists(full))Directory.Delete(full,true);}
    }
    [Fact]
    public void Typed_encoding_covers_effect_fields_and_budget_without_hidden_rng()
    {
        var rules=RuleCompiler.Fixture();var env=new GameEnvironment(rules);env.Reset(69,"fixture");var encoder=new PolicyEncoding(rules);
        var original=encoder.Observation(env.GetObservation(),1,64);var changed=encoder.Observation(env.GetObservation(),2,64);
        Assert.False(original.SequenceEqual(changed));Assert.True(PolicyEncoding.EffectFields.Length+4<=PolicyEncoding.TokenWidth);
        env.State.RandomState++;Assert.Equal(original,encoder.Observation(env.GetObservation(),1,64));
        var effect=new Effect(Trigger.None,Operation.Gold,Target.All,Amount:3,IntervalSeconds:2);
        var observation=env.GetObservation() with {Choices=[new("Choice",null,[effect])]};
        Assert.False(encoder.Observation(observation).SequenceEqual(encoder.Observation(observation with {Choices=[new("Choice",null,[effect with {IntervalSeconds=3}])]})));
        Assert.False(encoder.Observation(observation).SequenceEqual(encoder.Observation(observation with {Choices=[new("Choice",null,[effect with {Rarity=0}])]})));
    }
    [Fact]
    public void Combat_stat_cache_matches_uncached_and_reference_with_dynamic_effects()
    {
        var rules=RuleCompiler.Fixture();var species=rules.Species[0] with {Normal=[new(1,.5,[50,0,0,1,1,1,.5,2,0],
            [new(Trigger.Cast,Operation.Add,Target.All,Stat.Speed,.1),new(Trigger.Cast,Operation.Knockout,Target.RandomAlly),
             new(Trigger.Knockout,Operation.Revive,Target.Self,Stat.Damage,10),new(Trigger.Ongoing,Operation.Add,Target.All,Stat.Damage,.5,Scale:EffectScale.AlliedStat)])]};
        rules=rules with {Species=[species,..rules.Species.Skip(1)]};var combat=new Combat(rules);
        var board=new FrozenBoard(1,1000,"fixture",[new(){SpeciesId=species.Id},new(){SpeciesId=species.Id},null,null,null,null],[],[]);
        for(ulong seed=0;seed<20;seed++){
            var cached=combat.ResolveBattle(board,board,seed,true);var uncached=combat.ResolveBattle(board,board,seed,true,false,false);var reference=combat.ResolveBattle(board,board,seed,true,true);
            Assert.Equal(JsonSerializer.Serialize(uncached),JsonSerializer.Serialize(cached));Assert.Equal(cached.Trace,reference.Trace);
            Assert.Equal(cached.PlayerHp,reference.PlayerHp);Assert.Equal(cached.OpponentHp,reference.OpponentHp);
        }
    }
}
