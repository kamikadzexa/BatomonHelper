using BatomonHelper.Simulation;
using System.Diagnostics;
using System.Text.Json;

internal static class RepresentativeBenchmark
{
    public static void Run(string rulesPath,string checkpointPath,string output)
    {
        var rules=JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(rulesPath))!;
        var checkpoint=JsonSerializer.Deserialize<HostCheckpoint>(File.ReadAllText(checkpointPath))!;
        if(checkpoint.RulesHash!=rules.Hash)throw new InvalidDataException("Benchmark rules differ.");
        var encoder=new PolicyEncoding(rules);var legacy=new LegacyEncoding(rules);var combat=new Combat(rules);
        var results=new Dictionary<string,object>();
        foreach(var band in new[]{"early","middle","late"}){
            var boards=checkpoint.League.Where(x=>x.Kind!="baseline"&&(x.Board.Rank<=4?"early":x.Board.Rank<=8?"middle":"late")==band)
                .OrderByDescending(x=>x.Board.Team.OfType<Unit>().Sum(u=>u.Bonuses.Sum())).Take(6).Select(x=>x.Board).ToArray();
            if(boards.Length==0)continue;
            var states=checkpoint.States.Where(x=>(x.Rank<=4?"early":x.Rank<=8?"middle":"late")==band).Take(6).ToArray();
            var stats=new Dictionary<string,object>();
            stats["legacy_state_clone"]=Measure(states.Select<RunState,Action>(state=>()=>{_=JsonSerializer.Deserialize<RunState>(JsonSerializer.Serialize(state));}).ToArray(),10);
            stats["state_clone"]=Measure(states.Select<RunState,Action>(state=>()=>{_=state.Clone();}).ToArray(),10);
            stats["encoding"]=Measure(states.Select<RunState,Action>(state=>{var env=new GameEnvironment(rules);env.Restore(state);var observation=env.GetObservation();return ()=>{_=encoder.Observation(observation);};}).ToArray(),10);
            stats["legacy_encoding"]=Measure(states.Select<RunState,Action>(state=>{var env=new GameEnvironment(rules);env.Restore(state);var observation=env.GetObservation();return ()=>{_=legacy.Observation(observation);};}).ToArray(),10);
            stats["combat_uncached"]=Measure(boards.Select<FrozenBoard,Action>(board=>()=>{_=combat.ResolveBattle(board,board,69,false,false,false);}).ToArray(),3);
            stats["combat"]=Measure(boards.Select<FrozenBoard,Action>(board=>()=>{_=combat.ResolveBattle(board,board,69);}).ToArray(),3);
            foreach(var board in boards){var fast=combat.ResolveBattle(board,board,69,true);var reference=combat.ResolveBattle(board,board,69,true,true);
                if(fast.PlayerHp!=reference.PlayerHp||fast.OpponentHp!=reference.OpponentHp||!fast.Trace.SequenceEqual(reference.Trace))throw new InvalidOperationException("Representative reference trace mismatch.");}
            results[band]=stats;
        }
        var league=new OpponentLeague();league.Restore(checkpoint.League,checkpoint.Generation);
        results["indexed_opponent_lookup"]=Measure([()=>{for(var i=0;i<1000;i++)league.Pick(1+i%16,(ulong)i);}],10);
        results["legacy_opponent_scan"]=Measure([()=>{for(var i=0;i<1000;i++)_=checkpoint.League.Where(x=>x.Board.Day==1+i%16&&x.Generation==checkpoint.Generation).ToArray();}],10);
        var runs=0;var battles=0;var timer=Stopwatch.StartNew();
        for(var i=0;i<8;i++){var env=new GameEnvironment(rules);env.Reset((ulong)i+50000,rules.Trainers[i%rules.Trainers.Length].Id);var policy=new StrongestShopPolicy(rules);
            for(var decision=0;decision<3000&&env.State.Phase!=Phase.Complete;decision++){var move=policy.Choose(env);
                var step=env.Step(move,b=>league.Pick(b.Day,(ulong)i*1000+(ulong)b.Day,true)!.Board);if(step.DayRecord is not null)battles++;}
            if(env.State.Phase==Phase.Complete)runs++;
        }
        results["full_runs"]=new {Runs=runs,Battles=battles,Seconds=timer.Elapsed.TotalSeconds,RunsPerSecond=runs/timer.Elapsed.TotalSeconds,BattlesPerSecond=battles/timer.Elapsed.TotalSeconds};
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new {RulesHash=rules.Hash,LeagueBoards=checkpoint.League.Length,DiagnosticFixture=rules.Build=="fixture",Measurements=results},new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine("Representative benchmark saved to "+Path.GetFullPath(output));
    }
    private static object Measure(Action[] cases,int repetitions)
    {
        if(cases.Length==0)return new {Count=0};
        foreach(var action in cases)action();
        var times=new List<double>();var allocation=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<repetitions;i++)foreach(var action in cases){var start=Stopwatch.GetTimestamp();action();times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
        allocation=GC.GetAllocatedBytesForCurrentThread()-allocation;times.Sort();
        return new {Count=times.Count,Milliseconds=times.Sum(),MedianMilliseconds=times[times.Count/2],P95Milliseconds=times[(int)Math.Ceiling(times.Count*.95)-1],AllocatedBytes=allocation};
    }
}
