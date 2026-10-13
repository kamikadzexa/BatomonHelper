using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Diagnostics;

var json=new JsonSerializerOptions { PropertyNameCaseInsensitive=true };
if(args.Length>0 && args[0]=="--write-profile")
{
    await File.WriteAllTextAsync(args[1],JsonSerializer.Serialize(SimulationProfile.UserPantra(),new JsonSerializerOptions {WriteIndented=true}));return 0;
}
if(args.Length>0 && args[0]=="--review-sources")
{
    using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(60)};
    http.DefaultRequestHeaders.UserAgent.ParseAdd("BatomonHelper catalog bundler");
    var review=await SourceReviewer.FetchAsync(http);
    Directory.CreateDirectory(args[1]);var path=Path.Combine(args[1],"source-review.json");
    await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(review,new JsonSerializerOptions {WriteIndented=true}));
    File.Move(path+".tmp",path,true);
    Console.WriteLine(JsonSerializer.Serialize(new {review.Build,review.Balance,Pages=review.Pages.Length,Decisions=review.Decisions.Length}));return 0;
}
if(args.Length>0 && args[0]=="--refresh")
{
    Directory.CreateDirectory(args[1]);
    using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(60)};
    http.DefaultRequestHeaders.UserAgent.ParseAdd("BatomonHelper catalog bundler");
    var source=new BatomonWikiSource(http);
    var imported=await new WikiImporter(source).ImportAsync(CancellationToken.None);
    var catalog=imported.Snapshot??throw new InvalidDataException(string.Join("; ",imported.Errors));
    if(catalog.DeclaredSteamBuild!="25738032" || catalog.Balance!="25") throw new InvalidDataException("Wiki build changed; audit target before publishing.");
    var knowledge=await new StrategyKnowledgeImporter(source).ImportAsync(catalog);
    var catalogPath=Path.Combine(args[1],"catalog.db"); var repository=new SnapshotRepository(catalogPath);
    await repository.InitializeAsync(); await repository.SaveAsync(catalog);
    await File.WriteAllTextAsync(Path.Combine(args[1],"knowledge.json"),JsonSerializer.Serialize(knowledge with {Pairs=[],Compositions=[]},new JsonSerializerOptions {WriteIndented=true}));
    var profile=await LoadProfile(Path.Combine(args[1],"user-profile.json"));
    await File.WriteAllTextAsync(Path.Combine(args[1],"rules.json"),JsonSerializer.Serialize(RuleCompiler.Compile(catalog,knowledge,profile),new JsonSerializerOptions {WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(new { catalog.DeclaredSteamBuild,catalog.Balance,Creatures=catalog.Monsters.Length,Items=catalog.Items.Length,Trinkets=catalog.Trinkets.Length }));
    return 0;
}
if(args.Length==0) { Console.Error.WriteLine("--audit <catalog.db> <knowledge.json> <rules.json> | --review-sources <directory> | --fixture <rules.json> | --serve <rules.json> <buffer> <count> <workers> <seed> | --benchmark"); return 1; }
if(args[0]=="--audit")
{
    var catalog=await new SnapshotRepository(args[1],true).GetLatestAsync()??throw new InvalidDataException("Missing catalog.");
    var knowledge=JsonSerializer.Deserialize<StrategyKnowledge>(await File.ReadAllTextAsync(args[2]))!;
    var profile=await LoadProfile(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3]))!,"user-profile.json"));
    var compiled=RuleCompiler.Compile(catalog,knowledge,profile);
    await File.WriteAllTextAsync(args[3],JsonSerializer.Serialize(compiled,new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine(JsonSerializer.Serialize(new { compiled.Build,compiled.Balance,compiled.Ready,compiled.FullFidelityReady,compiled.GuessedRules,Issues=compiled.Issues.Length })); return 0;
}
if(args[0]=="--fixture") { await File.WriteAllTextAsync(args[1],JsonSerializer.Serialize(RuleCompiler.Fixture())); return 0; }
if(args[0]=="--benchmark")
{
    var r=RuleCompiler.Fixture(); var env=new GameEnvironment(r); env.Reset(11,"fixture");
    env.Step(env.GetLegalActions().First(x=>x.Kind==Move.Buy)); var b=FrozenBoard.From(env.State);
    var combat=new Combat(r); var a=combat.ResolveBattle(b,b,1,true); var reference=combat.ResolveBattle(b,b,1,true,true);
    if(a.PlayerHp!=reference.PlayerHp || !a.Trace.SequenceEqual(reference.Trace)) throw new InvalidOperationException("Reference trace mismatch.");
    var watch=Stopwatch.StartNew(); for(var i=0;i<1000;i++) combat.ResolveBattle(b,b,(ulong)i);
    Console.WriteLine(JsonSerializer.Serialize(new { DiagnosticFixture=true,Battles=1000,Milliseconds=watch.Elapsed.TotalMilliseconds })); return 0;
}
if(args[0]!="--serve") return 1;
var rules=JsonSerializer.Deserialize<RulesManifest>(await File.ReadAllTextAsync(args[1]),json)!;
try { rules.RequireReady(); } catch(Exception error) { Console.Error.WriteLine(error.Message); return 2; }
var count=int.Parse(args[3]); var workers=int.Parse(args[4]); var baseSeed=ulong.Parse(args[5]);
if(count<1 || count>4096 || workers<1) throw new ArgumentException("Invalid resource limits.");
var environments=Enumerable.Range(0,count).Select(_=>new GameEnvironment(rules)).ToArray();
var episodes=new long[count]; var records=Enumerable.Range(0,count).Select(_=>new List<DayRecord>()).ToArray();
var shopDecisions=new int[count];
SimAction[][]? publishedLegal=null;
var shopDecisionLimit=args.Length>11?int.Parse(args[11]):TrainingObjective.MaximumShopDecisions;
if(shopDecisionLimit<1)throw new ArgumentException("Shop decision limit must be positive.");
var encoder=new PolicyEncoding(rules); var generation=0; var league=new List<LeagueEntry>();
double lastEncodingMilliseconds=0,lastBufferMilliseconds=0;
var sessionId=args.Length>10?args[10]:Guid.NewGuid().ToString("N");
var snapshotUpdate=args.Length>12?int.Parse(args[12]):-1;
var evaluationBatch=args.Length>13&&args[13]!="none"?args[13]:null;
var snapshotGeneration=args.Length>14?int.Parse(args[14]):-1;
if(!System.Text.RegularExpressions.Regex.IsMatch(sessionId,"^[A-Za-z0-9_-]{1,80}$"))throw new InvalidDataException("Invalid training session identity.");
var evaluation=args.Length>7 && args[7].StartsWith("evaluation",StringComparison.Ordinal);
var initialEvaluation=args.Length>7 && args[7]=="evaluation-initial";
await using var history=new HistoryStore(args.Length>6?args[6]:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!,"history"));
var historyPath=args.Length>6?args[6]:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!,"history");
if(!System.Text.RegularExpressions.Regex.IsMatch(rules.Hash,"^[A-Za-z0-9_-]+$")) throw new InvalidDataException("Invalid rules identity.");
Atomic(Path.Combine(historyPath,"rules-"+rules.Hash+".json"),JsonSerializer.Serialize(rules));
var capacity=Math.Max(64L*1024*1024,(long)count*128*1024);
using var file=new FileStream(args[2],FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite); file.SetLength(capacity);
using var mapping=MemoryMappedFile.CreateFromFile(file,null,capacity,MemoryMappedFileAccess.ReadWrite,HandleInheritability.None,true);
using var view=mapping.CreateViewAccessor();
for(var i=0;i<count;i++) Reset(i);
// Seed day-matched opponents with legal, independent baseline runs. No arbitrary stat or item randomization.
for(var baseline=0;baseline<4;baseline++)
{
    var environment=new GameEnvironment(rules); environment.Reset(baseSeed+100000+(ulong)baseline,rules.Trainers[baseline%rules.Trainers.Length].Id);
    var decisionRandom=new SeedStream(baseSeed+900000+(ulong)baseline);
    for(var decision=0;decision<10000 && environment.State.Phase!=Phase.Complete;decision++)
    {
        var action=Baseline(environment,baseline,decisionRandom);
        if(action.Kind==Move.Finish) league.Add(new(-1,baseline,"baseline",FrozenBoard.From(environment.State)));
        environment.Step(action,b=>Pick(b.Day,(ulong)baseline,b));
    }
}
// A legal independent baseline deliberately loses six early battles, then plays ties.
// This supplies the longest normal-run days without ever using an evaluating policy's board.
for(var attempt=0;attempt<8 && !league.Any(x=>x.Kind=="baseline" && x.Board.Day==16);attempt++)
{
    var env=new GameEnvironment(rules);env.Reset(baseSeed+700000+(ulong)attempt,rules.Trainers.FirstOrDefault(x=>x.Id=="youngster-m")?.Id??rules.Trainers[0].Id);
    for(var decision=0;decision<1000 && env.State.Phase!=Phase.Complete;decision++){
        var legal=env.GetLegalActions();var choice=legal.FirstOrDefault(x=>x.Kind==Move.Choose);
        if(choice is not null){env.Step(choice);continue;}
        if(legal.FirstOrDefault(x=>x.Kind==Move.Sell) is { } sell){env.Step(sell);continue;}
        var board=FrozenBoard.From(env.State);league.Add(new(-1,3,"baseline",board));
        env.Step(new(Move.Finish),b=>env.State.Day<=6?league.Where(x=>x.Board.Day==b.Day).OrderByDescending(x=>
            x.Board.Team.OfType<Unit>().Sum(u=>LevelRules.Resolve(rules.Species.First(s=>s.Id==u.SpeciesId),u.Level,u.Shiny).Stats.Take(6).Sum()+u.Bonuses.Take(6).Sum())).First().Board:b);
    }
}
var workerTimings=new Dictionary<int,double>();
if(args.Length>8 && args[8]=="tune")
{
    var snapshots=environments.Select(x=>x.State.Clone()).ToArray();
    foreach(var candidate in new[]{4,8,Environment.ProcessorCount,workers}.Where(x=>x<=workers).Append(workers).Distinct())
    {
        var watch=Stopwatch.StartNew();
        Parallel.For(0,count,new ParallelOptions {MaxDegreeOfParallelism=candidate},i=>
        {
            var env=environments[i];
            var action=env.GetLegalActions().FirstOrDefault(x=>x.Kind==Move.Buy);
            if(action is not null)env.Step(action);
            if(env.State.Phase==Phase.Shop)env.Step(new(Move.Finish),b=>Pick(b.Day,(ulong)i,b));
            env.Restore(snapshots[i]);
        });
        workerTimings[candidate]=watch.Elapsed.TotalMilliseconds;
    }
    workers=workerTimings.MinBy(x=>x.Value).Key;
}
var environmentTimings=new Dictionary<int,double>();
if(args.Length>9 && args[9]=="tune-environments"){
    var snapshots=environments.Select(x=>x.State.Clone()).ToArray();
    foreach(var candidate in new[]{256,512,count}.Where(x=>x<=count).Distinct()){
        var watch=Stopwatch.StartNew();
        Parallel.For(0,candidate,new ParallelOptions{MaxDegreeOfParallelism=workers},i=>{
            var env=environments[i];var action=env.GetLegalActions().FirstOrDefault(x=>x.Kind==Move.Buy);if(action is not null)env.Step(action);
            if(env.State.Phase==Phase.Shop)env.Step(new(Move.Finish),b=>Pick(b.Day,(ulong)i,b));env.Restore(snapshots[i]);
        });environmentTimings[candidate]=watch.Elapsed.TotalMilliseconds/Math.Max(1,candidate);
    }
    count=environmentTimings.MinBy(x=>x.Value).Key;
    environments=environments.Take(count).ToArray();episodes=episodes.Take(count).ToArray();records=records.Take(count).ToArray();
    shopDecisions=shopDecisions.Take(count).ToArray();
}
Emit(new { Ready=true,Protocol=1,Count=count,Workers=workers,WorkerMilliseconds=workerTimings,EnvironmentMillisecondsPerRun=environmentTimings,ObservationWidth=encoder.ObservationWidth,ActionWidth=PolicyEncoding.ActionWidth,RulesHash=rules.Hash,Encoding=PolicyEncoding.Version,SimulationRevision=rules.SimulationRevision,TrainingObjective=TrainingObjective.Version,ShopDecisionLimit=shopDecisionLimit });
while(await Console.In.ReadLineAsync() is { } line)
{
    using var request=JsonDocument.Parse(line); var root=request.RootElement; var command=root.GetProperty("command").GetString();
    try
    {
        switch(command)
        {
            case "batch": Publish(); break;
            case "step":
                var stepWatch=Stopwatch.StartNew();
                var indices=root.GetProperty("actions").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
                if(indices.Length!=count) throw new ArgumentException("One action index per environment required.");
                var batchLegal=publishedLegal??Enumerable.Range(0,count).Select(i=>TrainingObjective.Candidates(environments[i],shopDecisions[i],shopDecisionLimit)).ToArray();
                if(indices.Where((value,i)=>value<0||value>=batchLegal[i].Length).Any())throw new ArgumentException("Action index outside legal set; batch was not applied.");
                publishedLegal=null;
                var results=new StepResult[count]; var saved=new RunRecord?[count]; var boards=new FrozenBoard?[count];
                var rewards=new double[count];var limited=new bool[count];
                var selectedKinds=new int[count];var improving=new bool[count];var reversing=new bool[count];var gapFinishes=new bool[count];
                Parallel.For(0,count,new ParallelOptions {MaxDegreeOfParallelism=workers},i=>
                {
                    var legal=batchLegal[i];
                    var action=legal[indices[i]];selectedKinds[i]=(int)action.Kind;
                    var gapBefore=TrainingObjective.DeployableBench(environments[i].State.Units);
                    var deploymentPotential=TrainingObjective.DeploymentPotential(environments[i].State.Units);
                    limited[i]=environments[i].State.Phase==Phase.Shop && shopDecisions[i]>=shopDecisionLimit;
                    if(action.Kind==Move.Finish){boards[i]=FrozenBoard.From(environments[i].State);gapFinishes[i]=gapBefore>0;}
                    results[i]=environments[i].Step(action,b=>Pick(b.Day,EvaluationSeed(i,b.Day),b));
                    if(action.Kind!=Move.Finish){var delta=TrainingObjective.DeploymentPotential(environments[i].State.Units)-deploymentPotential;
                        if(delta>1e-9)improving[i]=true;else if(delta< -1e-9)reversing[i]=true;}
                    rewards[i]=TrainingObjective.Reward(deploymentPotential,environments[i].State,results[i]);
                    shopDecisions[i]=results[i].DayRecord is not null?0:shopDecisions[i]+1;
                    if(results[i].DayRecord is { } day) records[i].Add(day);
                    if(results[i].Terminal)
                    {
                        var state=environments[i].State;
                        var summary=new RunSummary($"{(initialEvaluation?"initial-":"")}{sessionId}-{baseSeed:X16}-{i}-{episodes[i]}",evaluation&&snapshotGeneration>=0?snapshotGeneration:initialEvaluation?0:generation,i%4,state.Trainer,state.Wins,Math.Max(0,state.Lives),state.Day,
                            state.Wins>=rules.Parameters.WinTarget,evaluation,DateTimeOffset.UtcNow,rules.Hash,false,snapshotUpdate,evaluationBatch);
                        saved[i]=new(summary,records[i].ToArray()); Reset(i);
                    }
                });
                var actionCounts=Enum.GetValues<Move>().ToDictionary(x=>x.ToString(),x=>(long)selectedKinds.Count(value=>value==(int)x));
                var deploymentImproving=improving.LongCount(x=>x);var deploymentReversing=reversing.LongCount(x=>x);var finishWithGap=gapFinishes.LongCount(x=>x);
                var simulationMilliseconds=stepWatch.Elapsed.TotalMilliseconds;
                if(!evaluation) for(var i=0;i<count;i++) if(boards[i] is { } board) league.Add(new(generation,i%4,"current",board));
                TrimLeague();
                var historyWatch=Stopwatch.StartNew();
                foreach(var record in saved.OfType<RunRecord>()) await history.AppendAsync(record);
                var historyMilliseconds=historyWatch.Elapsed.TotalMilliseconds;
                var opponentResults=results.Select((result,i)=>(result,i)).Where(x=>x.result.DayRecord is not null).Select(x=>{
                    var day=x.result.DayRecord!;var entry=league.FirstOrDefault(e=>ReferenceEquals(e.Board,day.Opponent));
                    return new {Policy=x.i%4,Kind=entry?.Kind??"bootstrap",Generation=entry?.Generation??-1,Won=day.Result.PlayerWon};
                }).ToArray();
                Emit(new { Rewards=rewards,Terminals=results.Select(x=>x.Terminal),Runs=saved.OfType<RunRecord>().Select(x=>new {x.Summary}),Opponents=opponentResults,
                    Battles=results.Count(x=>x.DayRecord is not null),Deployed=boards.OfType<FrozenBoard>().Sum(x=>x.Team.Count(u=>u is not null)),
                    DeployableBench=results.Where(x=>x.DayRecord is not null).Sum(x=>Math.Min(x.DayRecord!.BeforeBattle.Units.Take(6).Count(u=>u is null),x.DayRecord.BeforeBattle.Units.Skip(6).Count(u=>u is not null))),
                    LimitedTurns=limited.Count(x=>x),ActionCounts=actionCounts,
                    DeploymentImproving=deploymentImproving,DeploymentReversing=deploymentReversing,FinishWithDeployableBench=finishWithGap,
                    PolicyBattleStats=Enumerable.Range(0,4).Select(policy=>new {Policy=policy,Battles=Enumerable.Range(0,count).Count(i=>i%4==policy&&results[i].DayRecord is not null),
                        Deployed=Enumerable.Range(0,count).Where(i=>i%4==policy&&boards[i] is not null).Sum(i=>boards[i]!.Team.Count(u=>u is not null)),
                        DeployableBench=Enumerable.Range(0,count).Where(i=>i%4==policy&&results[i].DayRecord is not null).Sum(i=>TrainingObjective.DeployableBench(results[i].DayRecord!.BeforeBattle.Units)),
                        LimitedTurns=Enumerable.Range(0,count).Count(i=>i%4==policy&&limited[i])}),
                    EncodingMilliseconds=lastEncodingMilliseconds,BufferMilliseconds=lastBufferMilliseconds,SimulationMilliseconds=simulationMilliseconds,HistoryMilliseconds=historyMilliseconds }); break;
            case "generation": generation=root.GetProperty("value").GetInt32(); TrimLeague(); Emit(new { Generation=generation }); break;
            case "checkpoint":
                await history.FlushAsync();
                var checkpoint=new HostCheckpoint(1,rules.Hash,generation,episodes,environments.Select(x=>x.State.Clone()).ToArray(),records.Select(x=>x.ToArray()).ToArray(),league.ToArray(),shopDecisions);
                Atomic(root.GetProperty("path").GetString()!,JsonSerializer.Serialize(checkpoint)); Emit(new { Saved=true }); break;
            case "restore":
                var restored=JsonSerializer.Deserialize<HostCheckpoint>(File.ReadAllText(root.GetProperty("path").GetString()!),json)!;
                if(restored.Version!=1||restored.RulesHash!=rules.Hash||restored.States.Length!=count) throw new InvalidDataException("Incompatible checkpoint.");
                generation=restored.Generation; episodes=restored.Episodes; league=restored.League.ToList();
                publishedLegal=null;
                shopDecisions=restored.ShopDecisions??new int[count];
                if(shopDecisions.Length!=count)throw new InvalidDataException("Incompatible shop decision counters.");
                for(var i=0;i<count;i++) { environments[i].Restore(restored.States[i]); records[i]=restored.Records[i].ToList(); }
                Emit(new { Restored=true }); break;
            case "league_restore":
                var source=JsonSerializer.Deserialize<HostCheckpoint>(File.ReadAllText(root.GetProperty("path").GetString()!),json)!;
                if(source.RulesHash!=rules.Hash) throw new InvalidDataException("Incompatible opponent archive.");
                var freshBaselines=league.Where(x=>x.Kind=="baseline").ToArray();
                league=source.League.Where(x=>x.Kind!="baseline").Concat(freshBaselines).ToList(); generation=source.Generation; Emit(new {Restored=true}); break;
            case "promote":
                var champion=root.GetProperty("policy").GetInt32();
                var championGeneration=root.TryGetProperty("generation",out var requestedGeneration)?requestedGeneration.GetInt32():generation;
                league.AddRange(league.Where(x=>x.Generation==championGeneration && x.Policy==champion && x.Kind=="current").Select(x=>x with {Kind="champion"}).ToArray());
                TrimLeague(); Emit(new {Promoted=true}); break;
            case "stop": Emit(new { Stopped=true }); return 0;
            default: throw new ArgumentException("Unknown command.");
        }
    }
    catch(Exception error) { Emit(new { Error=error.GetBaseException().Message }); }
}
return 0;

void Reset(int i)
{
    var ordinal=episodes[i]++*count+i;
    environments[i].Reset(baseSeed+(ulong)ordinal*0x9E3779B97F4A7C15UL,rules.Trainers[(int)((episodes[i]-1+i)%rules.Trainers.Length)].Id);
    records[i].Clear();
    shopDecisions[i]=0;
}
FrozenBoard Pick(int day,ulong seed,FrozenBoard fallback)
{
    var random=new SeedStream(seed^0xAE61UL); var roll=random.Uniform();
    var desired=roll<.4?"current":roll<.7?"recent":roll<.9?"champion":"baseline";
    // Archive categories are derived from frozen generation identity, never from mutable team strength.
    var matching=league.Where(x=>x.Board.Day==day && (desired switch {
        "baseline"=>x.Kind=="baseline", "recent"=>x.Generation>=generation-20 && x.Generation<generation,
        "champion"=>x.Kind=="champion", _=>x.Generation==generation })).ToArray();
    if(matching.Length==0) matching=league.Where(x=>x.Board.Day==day).ToArray();
    if(matching.Length==0 && evaluation)throw new InvalidOperationException("Independent same-day opponent archive missing for day "+day);
    return matching.Length==0?fallback:matching[random.Index(matching.Length)].Board;
}
ulong EvaluationSeed(int environment,int day)
{
    if(!evaluation)return environments[environment].State.RandomState;
    // Common random numbers: policy actions cannot perturb the evaluation opponent schedule.
    return TrainingObjective.EvaluationOpponentSeed(baseSeed,environment,episodes[environment],day);
}
void TrimLeague()
{
    var champions=league.Where(x=>x.Kind=="champion").Select(x=>(x.Generation,x.Policy)).Distinct().OrderByDescending(x=>x.Generation).Take(20).ToHashSet();
    league=league.Where(x=>x.Kind is "baseline" or "champion" || x.Generation>=generation-19)
        .Where(x=>x.Kind!="champion" || champions.Contains((x.Generation,x.Policy)))
        .GroupBy(x=>(x.Generation,x.Policy,x.Kind,x.Board.Day,x.Board.Trainer,Strength:Math.Min(2,x.Board.Wins*3/Math.Max(1,x.Board.Day))))
        .SelectMany(x=>x.TakeLast(2)).GroupBy(x=>(x.Generation,x.Policy,x.Kind,x.Board.Day)).SelectMany(x=>x.TakeLast(32)).ToList();
}
void Publish()
{
    var encodingWatch=Stopwatch.StartNew();
    var values=new float[count][];var actions=new SimAction[count][];var features=new float[count][];
    // JSON token encoding and candidate encoding are substantial CPU work. Each
    // worker owns its environment; the encoder's definition dictionaries are read-only.
    Parallel.For(0,count,new ParallelOptions {MaxDegreeOfParallelism=workers},i=>{
        var observation=environments[i].GetObservation();values[i]=encoder.Observation(observation);
        actions[i]=TrainingObjective.Candidates(environments[i],shopDecisions[i],shopDecisionLimit);
        var row=new float[actions[i].Length*PolicyEncoding.ActionWidth];
        for(var j=0;j<actions[i].Length;j++)encoder.Action(observation,actions[i][j],row.AsSpan(j*PolicyEncoding.ActionWidth,PolicyEncoding.ActionWidth));
        features[i]=row;
    });
    lastEncodingMilliseconds=encodingWatch.Elapsed.TotalMilliseconds;var bufferWatch=Stopwatch.StartNew();
    var width=values.Max(x=>x.Length);var counts=actions.Select(x=>x.Length).ToArray();
    var required=(long)count*(width+1)*4+counts.Sum(x=>(long)x)*PolicyEncoding.ActionWidth*4;
    if(required>capacity)throw new InvalidOperationException("Numeric buffer capacity exceeded; no state or action candidates have been discarded. Reduce environments.");
    long position=0;var flatObservations=new float[count*width];
    for(var i=0;i<count;i++)values[i].CopyTo(flatObservations,i*width);
    view.WriteArray(position,flatObservations,0,flatObservations.Length);position+=(long)flatObservations.Length*4;
    view.WriteArray(position,counts,0,count);position+=count*4;
    var flatActions=new float[counts.Sum()*PolicyEncoding.ActionWidth];var offset=0;
    for(var i=0;i<count;i++){features[i].CopyTo(flatActions,offset);offset+=features[i].Length;}
    view.WriteArray(position,flatActions,0,flatActions.Length);position+=(long)flatActions.Length*4;
    lastBufferMilliseconds=bufferWatch.Elapsed.TotalMilliseconds;
    publishedLegal=actions;
    Emit(new{Bytes=position,Count=count,ObservationWidth=width,ActionWidth=PolicyEncoding.ActionWidth,Encoding=PolicyEncoding.Version});
}
SimAction Baseline(GameEnvironment environment,int variant,SeedStream random)
{
    var legal=environment.GetLegalActions();
    if(variant==0)return legal[random.Index(legal.Length)];
    if(legal.FirstOrDefault(x=>x.Kind==Move.Choose) is { } choice)return choice;
    if(environment.State.Phase==Phase.Overflow)return legal.First(x=>x.Kind==Move.Sell);
    if(legal.FirstOrDefault(x=>x.Kind==Move.Merge) is { } merge)return merge;
    var purchases=legal.Where(x=>x.Kind==Move.Buy && x.Target is >=0 and <6).ToArray();
    if(purchases.Length==0)return legal.First(x=>x.Kind==Move.Finish);
    if(variant==1)return purchases.MaxBy(x=>
    {
        var u=environment.State.Shop[x.Source]!.Creature!;var species=rules.Species.First(s=>s.Id==u.SpeciesId);var level=species.Normal.Single(l=>l.Level==u.Level);
        return (level.Stats[0]+.5*level.Stats[2]+.5*level.Stats[1])/Math.Max(.1,level.Cooldown??10);
    })!;
    if(variant==2)return purchases.FirstOrDefault(x=>environment.State.Units.Any(u=>u?.SpeciesId==environment.State.Shop[x.Source]!.Creature!.SpeciesId))??purchases[0];
    return purchases.MinBy(x=>environment.State.Shop[x.Source]!.Price)!;
}
static void Atomic(string path,string content) { File.WriteAllText(path+".tmp",content); File.Move(path+".tmp",path,true); }
static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
static async Task<SimulationProfile> LoadProfile(string path)
{
    if(!File.Exists(path))await File.WriteAllTextAsync(path,JsonSerializer.Serialize(SimulationProfile.UserPantra(),new JsonSerializerOptions {WriteIndented=true}));
    return JsonSerializer.Deserialize<SimulationProfile>(await File.ReadAllTextAsync(path))??throw new InvalidDataException("Empty simulation profile.");
}
internal sealed record LeagueEntry(int Generation,int Policy,string Kind,FrozenBoard Board);
internal sealed record HostCheckpoint(int Version,string RulesHash,int Generation,long[] Episodes,RunState[] States,DayRecord[][] Records,LeagueEntry[] League,int[]? ShopDecisions=null);
