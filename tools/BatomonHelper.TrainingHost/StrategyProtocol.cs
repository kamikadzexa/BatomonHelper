using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Text.Json;

internal static class StrategyProtocol
{
    private static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    public static async Task<int> Run(string[] args){
        var rules=JsonSerializer.Deserialize<RulesManifest>(await File.ReadAllTextAsync(args[1]),Json)!;rules.RequireReady();
        var knowledge=File.Exists(args[2])?JsonSerializer.Deserialize<StrategyKnowledge>(await File.ReadAllTextAsync(args[2]),Json):null;
        var output=Path.GetFullPath(args[3]);Directory.CreateDirectory(output);var seed=ulong.Parse(args[4]);var suite=args.Length>5?args[5]:"training";
        if(suite is not ("training" or "development" or "final"))throw new ArgumentException("Unknown strategy suite.");
        var guide=new StrategyKnowledgeGuide(rules,knowledge,enabled:args.Length<=8||args[8]!="no-wiki");var search=new StrategySearch(rules,guide);var analyzer=new BoardAnalyzer(rules);var encoding=new PolicyEncoding(rules);
        var legacyEncoding=args.Length>7&&args[7]=="4"?new LegacyEncoding(rules):null;
        var archive=IndependentOpponentArchive.Build(rules,seed+(suite=="final"?2000000033UL:1000000007UL)).ToList();
        archive.AddRange(GuidedShopPolicy.Archive(rules,new(rules,knowledge),seed+(suite=="final"?3000000043UL:2000000011UL),archive));
        if(suite=="training"&&args.Length>6&&Directory.Exists(args[6]))archive.AddRange(new UserSessionStore(args[6],rules).Opponents());
        var frozenArchive=archive.ToArray();var poolRng=new SeedStream(seed^0xAE61UL);var episode=0;var decisions=0;var generation=0;
        var distribution=new StrategicOpponentPool(adaptive:suite=="training");
        var env=new GameEnvironment(rules);var memory=new ShopTurnMemory();var records=new List<DayRecord>();
        var baselinePolicy=new StrongestShopPolicy(rules);
        GuidedShopPolicy? teacher=null;
        var historyPath=args.Length>9?Path.GetFullPath(args[9]):Path.Combine(output,"history");var historySession=Guid.NewGuid().ToString("N");
        await using var history=new HistoryStore(historyPath);
        await File.WriteAllTextAsync(Path.Combine(historyPath,"rules-"+rules.Hash+".json"),JsonSerializer.Serialize(rules));
        var code=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(GameEnvironment).Assembly.Location).Concat(File.ReadAllBytes(typeof(StrategyProtocol).Assembly.Location)).ToArray()));
        var identity=new {Version=1,Rules=rules.Hash,Knowledge=guide.Identity,Suite=suite,Seed=seed,Code=code,Opponents=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(frozenArchive)))};
        await File.WriteAllTextAsync(Path.Combine(output,"suite.json"),JsonSerializer.Serialize(identity));
        Reset();Emit(new {Ready=true,Version=1,Encoding=5,ModelVersion=6,encoding.ObservationWidth,Species=rules.Species.Length,Identity=identity,Warnings=guide.Warnings});
        while(await Console.In.ReadLineAsync() is {} line){
            try{
                using var document=JsonDocument.Parse(line);var request=document.RootElement;var command=request.GetProperty("command").GetString();
                switch(command){
                    case "reset":
                        if(request.TryGetProperty("episode",out var specified))episode=specified.GetInt32();else episode++;
                        Reset();Emit(State());break;
                    case "observe":Emit(State());break;
                    case "visible":Emit(env.GetObservation());break;
                    case "position":
                        var position=request.GetProperty("observation").Deserialize<Observation>(Json)!;
                        env.Restore(StrategySearch.VisibleState(position,seed));memory.Reset();decisions=0;records.Clear();search.Clear();Emit(State());break;
                    case "analysis":Emit(analyzer.Analyze(FrozenBoard.From(env.State),samples:request.TryGetProperty("samples",out var samples)?samples.GetInt32():32));break;
                    case "baseline":Emit(new {Action=env.State.Phase==Phase.Shop&&decisions>=64?new SimAction(Move.Finish):baselinePolicy.Choose(env)});break;
                    case "teacher":Emit(new {Action=env.State.Phase==Phase.Shop&&decisions>=64?new SimAction(Move.Finish):teacher?.Choose(env)??baselinePolicy.Choose(env)});break;
                    case "plan":
                        var options=request.TryGetProperty("options",out var settings)?settings.Deserialize<SearchOptions>(Json)!:new SearchOptions();
                        var neural=request.TryGetProperty("neural",out var enabled)&&enabled.GetBoolean();
                        SearchPrediction[] Infer(SearchEvaluation[] evaluations){
                            Emit(new {Inference=evaluations});
                            var reply=Console.In.ReadLine()??throw new EndOfStreamException("Inference response missing.");
                            return JsonSerializer.Deserialize<SearchPrediction[]>(reply,Json)??throw new InvalidDataException("Inference response missing.");
                        }
                        Emit(search.Plan(env.GetObservation(),archive,options,neural?Infer:null,shopDecisions:decisions,turnMemory:memory,opponentPicker:(day,randomSeed)=>distribution.Pick(archive,day,randomSeed).Board));break;
                    case "step":
                        var action=request.GetProperty("action").Deserialize<SimAction>(Json)!;
                        var legal=memory.Filter(env.State,TrainingObjective.Candidates(env,decisions,64));
                        if(!legal.Contains(action))throw new ArgumentException("Action is outside the current legal set.");
                        var matchup=new SeedStream(seed^((ulong)episode*0x9E3779B97F4A7C15UL)^((ulong)env.State.Day*0xBF58476D1CE4E5B9UL));
                        LeagueEntry? fought=null;
                        var result=env.Step(action,b=>{fought=distribution.Pick(archive,b.Day,matchup.Next());return fought.Board;},matchup.Next());
                        if(result.Battle is {} fight&&fought is not null)distribution.Record(fought,fight.PlayerWon);
                        memory.Applied(action,env.State,result.Battle is not null);decisions=result.Battle is null?decisions+1:0;
                        if(result.DayRecord is {} day){
                            records.Add(StateSnapshots.Copy(day));
                            if(suite=="training"){
                                archive.Add(new(generation,0,"current",StateSnapshots.Board(day.BeforeBattle)));
                                var same=archive.Where(e=>e.Kind=="current"&&e.Board.Day==day.Day).ToArray();if(same.Length>256)archive.Remove(same[0]);
                            }
                        }
                        if(result.Terminal){
                            var summary=new RunSummary($"strategy-{suite}-{seed}-{episode}-{historySession}",generation,0,env.State.Trainer,env.State.Wins,Math.Max(0,rules.Parameters.EffectiveTurnCap-env.State.Day),env.State.Day,
                                result.Reward>0,suite!="training",DateTimeOffset.UtcNow,rules.Hash,SnapshotUpdate:generation,EvaluationBatch:suite,ScenarioId:episode);
                            await history.AppendAsync(new(summary,records.ToArray()));
                        }
                        Emit(new {State=State(),result.Reward,result.Terminal,Battle=result.Battle is null?null:new {result.Battle.PlayerWon,result.Battle.Seconds,
                            Deployed=result.DayRecord!.BeforeBattle.Units.Take(6).Count(u=>u is not null),UnusedBench=TrainingObjective.DeployableBench(result.DayRecord.BeforeBattle.Units)},Forced=decisions>=64});break;
                    case "generation":generation=request.GetProperty("value").GetInt32();search.Clear();Emit(new {generation});break;
                    case "matchups":Emit(distribution.Matchups);break;
                    case "checkpoint":
                        search.Clear(); // Resume and uninterrupted continuation both start with a fresh search tree.
                        var checkpoint=new StrategyHostCheckpoint(1,rules.Hash,guide.Identity,suite,seed,episode,decisions,generation,env.State.Clone(),memory.Visited,memory.LastAction,memory.RepeatedKind,poolRng.State,records.ToArray(),archive.ToArray(),baselinePolicy.Snapshot(),teacher?.Snapshot(),new(distribution.Matchups));
                        Emit(checkpoint);break;
                    case "restore":
                        var saved=request.GetProperty("checkpoint").Deserialize<StrategyHostCheckpoint>(Json)!;
                        if(saved.Version!=1||saved.Rules!=rules.Hash||saved.Knowledge!=guide.Identity||saved.Suite!=suite||saved.Seed!=seed)throw new InvalidDataException("Incompatible strategic checkpoint.");
                        env.Restore(saved.State);episode=saved.Episode;decisions=saved.Decisions;generation=saved.Generation;memory.Restore(saved.Visited,saved.Last,saved.Repeated);poolRng.State=saved.Random;
                        baselinePolicy=new(rules);if(saved.Baseline is {} baselineMemory)baselinePolicy.Restore(baselineMemory);
                        var resumeCores=guide.BuildCores;teacher=saved.Teacher is not null&&resumeCores.Length>0?new(rules,guide,resumeCores[episode%resumeCores.Length]):null;
                        if(teacher is not null)teacher.Restore(saved.Teacher!);search.Clear();
                        records=saved.Records.ToList();archive=saved.Archive.ToList();distribution.Restore(saved.Matchups??[]);Emit(State());break;
                    case "stop":Emit(new {Stopped=true});return 0;
                    default:throw new ArgumentException("Unknown strategic command.");
                }
            }catch(Exception error){Emit(new {Error=error.Message});}
        }
        return 0;
        void Reset(){env.Reset((seed+(ulong)episode*0x9E3779B97F4A7C15UL)^(suite=="final"?0xF19A134BE5UL:suite=="development"?0xD38E713AB9UL:0),rules.Trainers[episode%rules.Trainers.Length].Id);memory.Reset();baselinePolicy=new(rules);
            var cores=guide.BuildCores;teacher=cores.Length>0&&episode%4!=0?new(rules,guide,cores[episode%cores.Length]):null;
            decisions=0;records.Clear();search.Clear();}
        object State(){var o=env.GetObservation();var legal=memory.Filter(env.State,TrainingObjective.Candidates(env,decisions,64));
            return new {Observation=legacyEncoding?.Observation(o)??encoding.Observation(o,decisions,64,memory),Actions=legal,Features=legal.Select(a=>legacyEncoding?.Action(o,a)??encoding.Action(o,a)).ToArray(),o.Day,o.Wins,o.Trainer,o.Phase,Decisions=decisions,Episode=episode};}
    }
    private static void Emit(object value){Console.WriteLine(JsonSerializer.Serialize(value));Console.Out.Flush();}
    private sealed record StrategyHostCheckpoint(int Version,string Rules,string Knowledge,string Suite,ulong Seed,int Episode,int Decisions,int Generation,RunState State,string[] Visited,Move? Last,int Repeated,ulong Random,DayRecord[] Records,LeagueEntry[] Archive,ShopPolicyMemory? Baseline=null,GuidedPolicyMemory? Teacher=null,Dictionary<string,MatchupTotals>? Matchups=null);
}
