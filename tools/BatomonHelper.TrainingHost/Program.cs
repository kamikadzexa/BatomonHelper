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
if(args[0]=="--profile") {RepresentativeBenchmark.Run(args[1],args[2],args[3]);return 0;}
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
var encoder=new PolicyEncoding(rules);var legacyEncoder=new LegacyEncoding(rules);
var encodingVersion=args.Length>15?int.Parse(args[15]):PolicyEncoding.Version;
var generation=0;var league=new OpponentLeague();
var active=Enumerable.Repeat(true,count).ToArray();var scenarioIds=Enumerable.Range(0,count).Select(x=>(long)x).ToArray();var evaluationRuns=int.MaxValue;var episodeStarts=new long[3];RunState[]? evaluationStates=null;var startingRanks=new int[count];var startingWins=new int[count];
var memories=Enumerable.Range(0,count).Select(_=>new ShopTurnMemory()).ToArray();
var publications=new GameEnvironment.ActionPublication?[count];
var pickedEntries=new LeagueEntry?[count];
var curriculumMiddle=new List<RunState>();var curriculumLate=new List<RunState>();
var curriculum=args.Length>16&&args[16]=="curriculum";
long publicationRevision=0;long repeatedStatesBlocked=0;var evaluationBoards=new List<LeagueEntry>();
long lookupTicks=0;var rankStats=new Dictionary<string,RankBandMetrics>();
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
var capacity=Math.Max(64L*1024*1024,(long)count*256*1024);
using var file=new FileStream(args[2],FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite); file.SetLength(capacity);
using var mapping=MemoryMappedFile.CreateFromFile(file,null,capacity,MemoryMappedFileAccess.ReadWrite,HandleInheritability.None,true);
using var view=mapping.CreateViewAccessor();
for(var i=0;i<count;i++) Reset(i);
foreach(var entry in IndependentOpponentArchive.Build(rules,baseSeed))league.Add(entry);
var userPool=args.Length>17?args[17]:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!,"..","..","work","user-sessions");
foreach(var entry in new UserSessionStore(userPool,rules).Opponents())league.Add(entry);
var strongestPolicies=Enumerable.Range(0,count).Select(_=>new StrongestShopPolicy(rules)).ToArray();
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
Emit(new { Ready=true,Protocol=2,Count=count,Workers=workers,WorkerMilliseconds=workerTimings,EnvironmentMillisecondsPerRun=environmentTimings,ObservationWidth=encoder.ObservationWidth,ActionWidth=PolicyEncoding.ActionWidth,RulesHash=rules.Hash,Encoding=encodingVersion,SimulationRevision=rules.SimulationRevision,TrainingObjective=TrainingObjective.Version,ShopDecisionLimit=shopDecisionLimit });
while(await Console.In.ReadLineAsync() is { } line)
{
    using var request=JsonDocument.Parse(line); var root=request.RootElement; var command=root.GetProperty("command").GetString();
    try
    {
        switch(command)
        {
            case "batch": Publish(); break;
            case "baseline_actions":
                Emit(new {Actions=Enumerable.Range(0,count).Select(i=>active[i]?Array.IndexOf(publishedLegal![i],strongestPolicies[i].Choose(environments[i])):-1).ToArray()});break;
            case "step":
                if(publishedLegal is null || !root.TryGetProperty("revision",out var revision) || revision.GetInt64()!=publicationRevision)throw new ArgumentException("Stale batch revision.");
                var stepWatch=Stopwatch.StartNew();
                var indices=root.GetProperty("actions").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
                if(indices.Length!=count) throw new ArgumentException("One action index per environment required.");
                var batchLegal=publishedLegal??Enumerable.Range(0,count).Select(i=>TrainingObjective.Candidates(environments[i],shopDecisions[i],shopDecisionLimit)).ToArray();
                if(indices.Where((value,i)=>active[i]&&(value<0||value>=batchLegal[i].Length)).Any())throw new ArgumentException("Action index outside legal set; batch was not applied.");
                publishedLegal=null;
                var results=new StepResult[count]; var saved=new RunRecord?[count]; var boards=new FrozenBoard?[count];
                var rewards=new double[count];var limited=new bool[count];
                var selectedKinds=Enumerable.Repeat(-1,count).ToArray();var improving=new bool[count];var reversing=new bool[count];var gapFinishes=new bool[count];
                Parallel.For(0,count,new ParallelOptions {MaxDegreeOfParallelism=workers},i=>
                {
                    if(!active[i]){results[i]=new(null!,0,false);return;}
                    var legal=batchLegal[i];
                    var action=legal[indices[i]];selectedKinds[i]=(int)action.Kind;
                    var gapBefore=TrainingObjective.DeployableBench(environments[i].State.Units);
                    var deploymentPotential=TrainingObjective.DeploymentPotential(environments[i].State.Units);
                    limited[i]=environments[i].State.Phase==Phase.Shop && shopDecisions[i]>=shopDecisionLimit;
                    if(action.Kind==Move.Finish){boards[i]=FrozenBoard.From(environments[i].State);gapFinishes[i]=gapBefore>0;}
                    var stateBefore=environments[i].State;var phaseBefore=stateBefore.Phase;
                    var band=stateBefore.Rank<=4?"early":stateBefore.Rank<=8?"middle":"late";
                    var goldBefore=stateBefore.Gold;var rankBefore=stateBefore.Rank;
                    var selectedEntry=(LeagueEntry?)null;
                    results[i]=environments[i].StepPublished(publications[i]!,indices[i],b=>{
                        var lookupStart=Stopwatch.GetTimestamp();selectedEntry=league.Pick(b.Day,EvaluationSeed(i,b.Day),evaluation);
                        Interlocked.Add(ref lookupTicks,Stopwatch.GetTimestamp()-lookupStart);return selectedEntry?.Board??b;
                    },evaluation?EvaluationSeed(i,stateBefore.Day)^0xDA1CEUL:null);
                    pickedEntries[i]=selectedEntry;
                    memories[i].Applied(action,environments[i].State,results[i].DayRecord is not null);
                    lock(rankStats){if(!rankStats.TryGetValue(band,out var stats))rankStats[band]=stats=new();
                        stats.Decisions++;if(action.Kind==Move.Buy)stats.Purchases++;if(action.Kind==Move.Merge)stats.Merges++;
                        if(action.Kind==Move.Sell&&action.Source<6)stats.Replacements++;
                        if(results[i].DayRecord is not null){stats.Battles++;stats.Gold+=goldBefore;if(limited[i])stats.ForcedFinishes++;
                            if(goldBefore>=3&&legal.Any(x=>x.Kind==Move.Buy))stats.AffordablePurchasesSkipped++;}}
                    if(curriculum&&!evaluation&&results[i].DayRecord is not null&&environments[i].State.Phase==Phase.Shop){
                        var state=environments[i].State;var pool=state.Rank>=9?curriculumLate:state.Rank>=5?curriculumMiddle:null;
                        if(pool is not null)lock(pool){pool.Add(state.Clone());if(pool.Count>512)pool.RemoveAt(0);}}

                    if(action.Kind!=Move.Finish){var delta=TrainingObjective.DeploymentPotential(environments[i].State.Units)-deploymentPotential;
                        if(delta>1e-9)improving[i]=true;else if(delta< -1e-9)reversing[i]=true;}
                    rewards[i]=TrainingObjective.Reward(deploymentPotential,environments[i].State,results[i]);
                    shopDecisions[i]=results[i].DayRecord is not null?0:encodingVersion==4||phaseBefore==Phase.Shop?shopDecisions[i]+1:shopDecisions[i];
                    if(results[i].DayRecord is { } day){records[i].Add(day);boards[i]=StateSnapshots.Board(day.BeforeBattle);}
                    if(results[i].Terminal)
                    {
                        var state=environments[i].State;
                        var summary=new RunSummary($"{(initialEvaluation?"initial-":"")}{sessionId}-{baseSeed:X16}-{i}-{episodes[i]}",evaluation&&snapshotGeneration>=0?snapshotGeneration:initialEvaluation?0:generation,i%4,state.Trainer,state.Wins,Math.Max(0,state.Lives),state.Day,
                            state.Wins>=rules.Parameters.WinTarget,evaluation,DateTimeOffset.UtcNow,rules.Hash,false,snapshotUpdate,evaluationBatch,evaluation?scenarioIds[i]:null,startingRanks[i],startingWins[i]);
                        saved[i]=new(summary,records[i].ToArray());
                        if(evaluation){scenarioIds[i]+=count;if(scenarioIds[i]>=evaluationRuns)active[i]=false;else Reset(i);}
                        else Reset(i);
                    }
                });
                var actionCounts=Enum.GetValues<Move>().ToDictionary(x=>x.ToString(),x=>(long)selectedKinds.Count(value=>value==(int)x));
                var deploymentImproving=improving.LongCount(x=>x);var deploymentReversing=reversing.LongCount(x=>x);var finishWithGap=gapFinishes.LongCount(x=>x);
                var simulationMilliseconds=stepWatch.Elapsed.TotalMilliseconds;
                var leagueStart=Stopwatch.GetTimestamp();
                if(!evaluation) for(var i=0;i<count;i++) if(boards[i] is { } board) league.Add(new(generation,i%4,"current",board));
                if(evaluation)for(var i=0;i<count;i++)if(boards[i] is {} board)evaluationBoards.Add(new(snapshotGeneration,i%4,"champion",board));
                var leagueMilliseconds=Stopwatch.GetElapsedTime(leagueStart).TotalMilliseconds;
                var timing=new {LegalActionsMilliseconds=environments.Sum(x=>x.Timings.LegalActionsMilliseconds),StateCopyMilliseconds=environments.Sum(x=>x.Timings.StateCopyMilliseconds),CombatMilliseconds=environments.Sum(x=>x.Timings.CombatMilliseconds)};
                foreach(var env in environments)env.Timings.Clear();
                var historyWatch=Stopwatch.StartNew();
                foreach(var record in saved.OfType<RunRecord>()) await history.AppendAsync(record);
                var historyMilliseconds=historyWatch.Elapsed.TotalMilliseconds;
                var opponentResults=results.Select((result,i)=>(result,i)).Where(x=>x.result.DayRecord is not null).Select(x=>{
                    var day=x.result.DayRecord!;var entry=pickedEntries[x.i];
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
                    EpisodeStarts=episodeStarts,HostStages=timing,Active=active,BattleBoundaries=results.Select(x=>x.DayRecord is not null),RankBands=rankStats,RepeatedStatesBlocked=repeatedStatesBlocked,OpponentLookupMilliseconds=Interlocked.Exchange(ref lookupTicks,0)*1000d/Stopwatch.Frequency,LeagueMilliseconds=leagueMilliseconds,EncodingMilliseconds=lastEncodingMilliseconds,BufferMilliseconds=lastBufferMilliseconds,SimulationMilliseconds=simulationMilliseconds,HistoryMilliseconds=historyMilliseconds }); break;
            case "evaluation_reset":
                if(!evaluation)throw new InvalidOperationException("Evaluation reset is only available for evaluation hosts.");
                evaluationRuns=root.GetProperty("runs").GetInt32();
                if(evaluationRuns<4||evaluationRuns%4!=0)throw new ArgumentException("Evaluation runs must be a positive multiple of four.");
                evaluationStates=null;
                if(root.TryGetProperty("states",out var statesPath)&&statesPath.ValueKind==JsonValueKind.String){
                    using var bank=JsonDocument.Parse(File.ReadAllText(statesPath.GetString()!));
                    if(bank.RootElement.GetProperty("rules_hash").GetString()!=rules.Hash)throw new InvalidDataException("Scenario rules differ.");
                    evaluationStates=bank.RootElement.GetProperty("scenarios").EnumerateArray().Select(x=>JsonSerializer.Deserialize<RunState>(x.GetProperty("state").GetRawText(),json)!).ToArray();
                    if(evaluationStates.Length==0)throw new InvalidDataException("Empty scenario bank.");
                }
                snapshotUpdate=root.GetProperty("update").GetInt32();snapshotGeneration=root.GetProperty("generation").GetInt32();
                encodingVersion=root.GetProperty("encoding").GetInt32();initialEvaluation=root.GetProperty("initial").GetBoolean();
                sessionId=root.GetProperty("session").GetString()!;Array.Clear(episodes);rankStats.Clear();evaluationBoards.Clear();repeatedStatesBlocked=0;publications=new GameEnvironment.ActionPublication?[count];publishedLegal=null;
                for(var i=0;i<count;i++){scenarioIds[i]=i;active[i]=i<evaluationRuns;Reset(i);strongestPolicies[i]=new StrongestShopPolicy(rules);}
                Emit(new {Reset=true,Encoding=encodingVersion});break;
            case "evaluation_boards":Emit(new {Boards=evaluationBoards});break;
            case "champion_boards":
                var champions=JsonSerializer.Deserialize<LeagueEntry[]>(root.GetProperty("entries").GetRawText(),json)!;
                foreach(var championEntry in champions){if(championEntry.Kind!="champion")throw new ArgumentException("Expected champion entries.");league.Add(championEntry);}
                TrimLeague();Emit(new {Promoted=true});break;
            case "generation": generation=root.GetProperty("value").GetInt32(); TrimLeague(); Emit(new { Generation=generation }); break;
            case "checkpoint":
                await history.FlushAsync();
                var checkpoint=new HostCheckpoint(1,rules.Hash,generation,episodes,environments.Select(x=>x.State.Clone()).ToArray(),records.Select(x=>x.ToArray()).ToArray(),league.Entries,shopDecisions,memories.Select(x=>new TurnMemorySnapshot(x.Visited,x.LastAction,x.RepeatedKind)).ToArray(),curriculumMiddle.ToArray(),curriculumLate.ToArray(),startingRanks,startingWins,episodeStarts);
                Atomic(root.GetProperty("path").GetString()!,JsonSerializer.Serialize(checkpoint)); Emit(new { Saved=true }); break;
            case "restore":
                var restored=JsonSerializer.Deserialize<HostCheckpoint>(File.ReadAllText(root.GetProperty("path").GetString()!),json)!;
                if(restored.Version!=1||restored.RulesHash!=rules.Hash||restored.States.Length!=count) throw new InvalidDataException("Incompatible checkpoint.");
                var activeUserBoards=league.Entries.Where(x=>x.Kind=="user").ToArray();
                generation=restored.Generation; episodes=restored.Episodes;league.Restore(restored.League.Where(x=>x.Kind!="user").Concat(activeUserBoards),generation);
                publishedLegal=null;
                shopDecisions=restored.ShopDecisions??new int[count];
                if(shopDecisions.Length!=count)throw new InvalidDataException("Incompatible shop decision counters.");
                for(var i=0;i<count;i++) { environments[i].Restore(restored.States[i]); records[i]=restored.Records[i].ToList();
                    var memory=restored.TurnMemories?.ElementAtOrDefault(i);if(memory is not null)memories[i].Restore(memory.Visited,memory.Last,memory.Repeated); }
                curriculumMiddle=restored.MiddleStarts?.ToList()??[];curriculumLate=restored.LateStarts?.ToList()??[];
                startingRanks=restored.StartingRanks??records.Select((x,i)=>x.FirstOrDefault()?.BeforeBattle.Rank??environments[i].State.Rank).ToArray();
                startingWins=restored.StartingWins??records.Select((x,i)=>x.FirstOrDefault()?.BeforeBattle.Wins??environments[i].State.Wins).ToArray();
                episodeStarts=restored.EpisodeStarts??new long[3];
                Emit(new { Restored=true }); break;
            case "league_restore":
                var source=JsonSerializer.Deserialize<HostCheckpoint>(File.ReadAllText(root.GetProperty("path").GetString()!),json)!;
                if(source.RulesHash!=rules.Hash) throw new InvalidDataException("Incompatible opponent archive.");
                var freshBaselines=league.Entries.Where(x=>x.Kind is "baseline" or "user").ToArray();
                league.Restore(source.League.Where(x=>x.Kind is not ("baseline" or "user")).Concat(freshBaselines),source.Generation);generation=source.Generation; Emit(new {Restored=true}); break;
            case "promote":
                var champion=root.GetProperty("policy").GetInt32();
                var championGeneration=root.TryGetProperty("generation",out var requestedGeneration)?requestedGeneration.GetInt32():generation;
                foreach(var entry in league.Entries.Where(x=>x.Generation==championGeneration && x.Policy==champion && x.Kind=="current").ToArray())league.Add(entry with {Kind="champion"});
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
    var ordinal=episodes[i]++*count+i;var scenario=evaluation?scenarioIds[i]:ordinal;
    environments[i].Reset(baseSeed+(ulong)scenario*0x9E3779B97F4A7C15UL,rules.Trainers[(int)(scenario%rules.Trainers.Length)].Id);
    memories[i].Reset();
    if(curriculum&&!evaluation){var rng=new SeedStream(baseSeed^(ulong)ordinal*0xFA123UL);var roll=rng.Uniform();var pool=roll<.5?null:roll<.75?curriculumMiddle:curriculumLate;
        if(pool is not null)lock(pool)if(pool.Count>0)environments[i].Restore(pool[rng.Index(pool.Count)]); }
    if(evaluation&&evaluationStates is not null)environments[i].Restore(evaluationStates[(int)(scenarioIds[i]/4%evaluationStates.Length)]);
    startingRanks[i]=environments[i].State.Rank;startingWins[i]=environments[i].State.Wins;
    Interlocked.Increment(ref episodeStarts[startingRanks[i]==1?0:startingRanks[i]<9?1:2]);
    records[i].Clear();
    shopDecisions[i]=0;
}
FrozenBoard Pick(int day,ulong seed,FrozenBoard fallback)
{
    var start=Stopwatch.GetTimestamp();var entry=league.Pick(day,seed,evaluation);
    Interlocked.Add(ref lookupTicks,Stopwatch.GetTimestamp()-start);
    return entry?.Board??fallback;
}
ulong EvaluationSeed(int environment,int day)=>evaluation?
    TrainingObjective.EvaluationOpponentSeed(baseSeed,0,scenarioIds[environment],day):environments[environment].State.RandomState;
void TrimLeague()=>league.SetGeneration(generation);
void Publish()
{
    var encodingWatch=Stopwatch.StartNew();
    var values=new float[count][];var actions=new SimAction[count][];var features=new float[count][];
    // JSON token encoding and candidate encoding are substantial CPU work. Each
    // worker owns its environment; the encoder's definition dictionaries are read-only.
    Parallel.For(0,count,new ParallelOptions {MaxDegreeOfParallelism=workers},i=>{
        var observation=environments[i].GetObservation();values[i]=encodingVersion==4?legacyEncoder.Observation(observation):encoder.Observation(observation,shopDecisions[i],shopDecisionLimit,memories[i]);
        if(active[i]){publications[i]=environments[i].PublishActions(legal=>{
            var limited=TrainingObjective.Candidates(environments[i].State.Phase,legal,shopDecisions[i],shopDecisionLimit,encodingVersion!=4);
            if(encodingVersion==4)return limited;
            var filtered=memories[i].Filter(environments[i].State,limited);Interlocked.Add(ref repeatedStatesBlocked,limited.Length-filtered.Length);return filtered;
        });actions[i]=publications[i]!.Actions.ToArray();}else actions[i]=[new(Move.Finish)];
        var row=new float[actions[i].Length*PolicyEncoding.ActionWidth];
        for(var j=0;j<actions[i].Length;j++)if(encodingVersion==4)legacyEncoder.Action(observation,actions[i][j],row.AsSpan(j*PolicyEncoding.ActionWidth,PolicyEncoding.ActionWidth));else encoder.Action(observation,actions[i][j],row.AsSpan(j*PolicyEncoding.ActionWidth,PolicyEncoding.ActionWidth));
        features[i]=row;
    });
    lastEncodingMilliseconds=encodingWatch.Elapsed.TotalMilliseconds;var bufferWatch=Stopwatch.StartNew();
    var width=values.Max(x=>x.Length);var counts=actions.Select(x=>x.Length).ToArray();
    var lengths=values.Select(x=>x.Length).ToArray();
    var required=(long)count*8+lengths.Sum(x=>(long)x)*4+counts.Sum(x=>(long)x)*PolicyEncoding.ActionWidth*4;
    if(required>capacity)throw new InvalidOperationException("Numeric buffer capacity exceeded; no state or action candidates have been discarded. Reduce environments.");
    long position=0;view.WriteArray(position,lengths,0,count);position+=count*4;
    var flatObservations=new float[lengths.Sum()];var observationOffset=0;
    for(var i=0;i<count;i++){values[i].CopyTo(flatObservations,observationOffset);observationOffset+=lengths[i];}
    view.WriteArray(position,flatObservations,0,flatObservations.Length);position+=(long)flatObservations.Length*4;
    view.WriteArray(position,counts,0,count);position+=count*4;
    var flatActions=new float[counts.Sum()*PolicyEncoding.ActionWidth];var offset=0;
    for(var i=0;i<count;i++){features[i].CopyTo(flatActions,offset);offset+=features[i].Length;}
    view.WriteArray(position,flatActions,0,flatActions.Length);position+=(long)flatActions.Length*4;
    lastBufferMilliseconds=bufferWatch.Elapsed.TotalMilliseconds;
    publishedLegal=actions;
    Emit(new{ScenarioIds=scenarioIds,Days=environments.Select(x=>x.State.Day),DecisionOrdinals=shopDecisions,Layout="ragged",Revision=++publicationRevision,Bytes=position,ObservationFloats=flatObservations.Length,Count=count,ObservationWidth=width,ActionWidth=PolicyEncoding.ActionWidth,Encoding=encodingVersion});
}
static void Atomic(string path,string content) { File.WriteAllText(path+".tmp",content); File.Move(path+".tmp",path,true); }
static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
static async Task<SimulationProfile> LoadProfile(string path)
{
    if(!File.Exists(path))await File.WriteAllTextAsync(path,JsonSerializer.Serialize(SimulationProfile.UserPantra(),new JsonSerializerOptions {WriteIndented=true}));
    return JsonSerializer.Deserialize<SimulationProfile>(await File.ReadAllTextAsync(path))??throw new InvalidDataException("Empty simulation profile.");
}
internal sealed class RankBandMetrics {public long Decisions {get;set;}public long Purchases {get;set;}public long Merges {get;set;}public long Replacements {get;set;}public long Battles {get;set;}public double Gold {get;set;}public long ForcedFinishes {get;set;}public long AffordablePurchasesSkipped {get;set;}public double AverageGold=>Gold/Math.Max(1,Battles);public double ForcedFinishRate=>ForcedFinishes/(double)Math.Max(1,Battles);}
internal sealed record TurnMemorySnapshot(string[] Visited,Move? Last,int Repeated);
internal sealed record HostCheckpoint(int Version,string RulesHash,int Generation,long[] Episodes,RunState[] States,DayRecord[][] Records,LeagueEntry[] League,int[]? ShopDecisions=null,TurnMemorySnapshot[]? TurnMemories=null,RunState[]? MiddleStarts=null,RunState[]? LateStarts=null,int[]? StartingRanks=null,int[]? StartingWins=null,long[]? EpisodeStarts=null);
