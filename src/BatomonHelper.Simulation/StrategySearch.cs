using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatomonHelper.Simulation;

public sealed record SearchOptions(double Seconds=30,int Simulations=512,int BatchSize=8,int HorizonDays=2,int MaximumDepth=96,ulong Seed=69,int AnalysisSamples=4);
public sealed record SearchEvaluation(float[] Observation,float[][] Actions,float[] Analysis,double[] GuidePriors);
public sealed record SearchPrediction(double[] Priors,double Value);
public sealed record SearchAlternative(SimAction Action,int Visits,double Value,double Probability);
public sealed record SearchContinuation(string ObservationIdentity,SimAction Action);
public sealed record SearchResult(int Version,SimAction Action,SearchAlternative[] Alternatives,int Simulations,double Seconds,bool TimedOut,
    float[] Observation,float[][] Actions,float[] Analysis,double[] Visits,StrategyOpportunity[] Opportunities,string[] Limitations,SearchContinuation[]? Continuation=null);

/// <summary>Chance outcomes have separate children; selection cannot inspect the live RNG or future opponent.</summary>
public sealed class StrategySearch(RulesManifest rules,StrategyKnowledgeGuide guide)
{
    private readonly PolicyEncoding _encoding=new(rules);
    private readonly BoardAnalyzer _analyzer=new(rules);
    private Node? _previousRoot;
    public void Clear()=>_previousRoot=null;
    public static string PositionIdentity(Observation observation)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(observation)));
    public static RunState VisibleState(Observation o,ulong seed){
        var v=o.VisibleState;
        var state=new RunState {Day=o.Day,Lives=o.Lives,Wins=o.Wins,Gold=o.Gold,MaxHp=o.MaxHp,Rank=o.Rank,Trainer=o.Trainer,
            Units=o.Units.Select(u=>u?.Clone()).ToArray(),Shop=o.Shop.Select(s=>s is null?null:s with {Creature=s.Creature?.Clone()}).ToArray(),Bag=[..o.Bag],ExtraTrainers=[..o.ExtraTrainers],
            Counters=o.Counters is null?[]:new(o.Counters),Locked=o.Locked,FreeRerolls=o.FreeRerolls,ItemUses=o.ItemUses,MaximumItemUses=o.MaximumItemUses,Phase=o.Phase,
            Choices=o.Choices.Select(RunState.CopyChoice).ToArray(),Overflow=o.Overflow?.Clone(),RandomState=seed,
            ForeignPool=v?.ForeignPool??[],CarryShop=v?.CarryShop??false,SecondChanceUsed=v?.SecondChanceUsed??false,PendingUnits=v?.PendingUnits.Select(u=>u.Clone()).ToArray()??[],
            TakeAllChoices=v?.TakeAllChoices??false,PendingShopEffects=v?.PendingShopEffects??[],PendingPurchaseBuffs=v?.PendingPurchaseBuffs??[],PendingShopStart=v?.PendingShopStart??false,
            ActiveChoicePhase=v?.ActiveChoicePhase??Phase.Reward,CurrentEvent=v?.CurrentEvent};
        IEnumerable<Unit> All(Unit u){yield return u;if(u.Devoured is {} child)foreach(var c in All(child))yield return c;}
        state.NextInstance=state.Units.OfType<Unit>().Concat(state.Shop.Select(x=>x?.Creature).OfType<Unit>()).Concat(state.PendingUnits).Concat(state.Overflow is {} overflow?[overflow]:[]).SelectMany(All).Select(x=>x.Instance).DefaultIfEmpty(0).Max()+1;
        state.NextInventoryInstance=state.Bag.Select(b=>b.Instance).DefaultIfEmpty(0).Max()+1;
        return state;
    }
    public SearchResult Plan(Observation observation,IReadOnlyList<LeagueEntry> opponents,SearchOptions? options=null,
        Func<SearchEvaluation[],SearchPrediction[]>? evaluate=null,CancellationToken cancellation=default,int shopDecisions=0,ShopTurnMemory? turnMemory=null,Func<int,ulong,FrozenBoard>? opponentPicker=null){
        options??=new();
        if(options.Seconds<=0||!double.IsFinite(options.Seconds)||options.Simulations<1||options.BatchSize<1||options.HorizonDays<1||options.MaximumDepth<1||options.AnalysisSamples is <1 or >256)throw new ArgumentException("Invalid search budget.");
        var watch=Stopwatch.StartNew();var random=new SeedStream(options.Seed);var initial=VisibleState(observation,random.Next());
        var env=new GameEnvironment(rules);env.Restore(initial);
        var memory=new ShopTurnMemory();if(turnMemory is not null)memory.Restore(turnMemory.Visited,turnMemory.LastAction,turnMemory.RepeatedKind);
        var rootKey=Key(observation,shopDecisions,memory);
        var root=_previousRoot?.Edges.SelectMany(e=>e.Children).FirstOrDefault(pair=>pair.Key==rootKey).Value??new Node();
        var rootMemory=memory;var analyses=new Dictionary<string,float[]>();
        var rootFeatures=Analysis(observation);if(root.Edges.Length==0)Expand(root,observation,Legal(env,memory,shopDecisions),shopDecisions,memory);
        Predict([(root,observation,rootFeatures)],true);
        var simulations=0;
        while(simulations<options.Simulations&&watch.Elapsed.TotalSeconds<options.Seconds){
            cancellation.ThrowIfCancellationRequested();var pending=new List<(Node Node,Observation Observation,float[] Analysis)>();
            var paths=new List<(List<Edge> Edges,Node Leaf,double? Terminal)>();
            for(var batch=0;batch<options.BatchSize&&simulations+paths.Count<options.Simulations;batch++){
                if(watch.Elapsed.TotalSeconds>=options.Seconds&&paths.Count>0)break;
                var state=VisibleState(observation,random.Next());env.Restore(state);memory=new();memory.Restore(rootMemory.Visited,rootMemory.LastAction,rootMemory.RepeatedKind);
                var node=root;var path=new List<Edge>();var visited=new HashSet<string>();var decisions=shopDecisions;double? terminal=null;
                for(var depth=0;depth<options.MaximumDepth;depth++){
                    if(node.Edges.Length==0){terminal=env.State.Wins>=rules.Parameters.WinTarget?1:0;break;}
                    var width=Math.Min(node.Edges.Length,8+(int)Math.Sqrt(node.Visits+1));
                    var legal=Legal(env,memory,decisions).ToHashSet();
                    var ranked=node.Edges.OrderByDescending(e=>e.Prior).Where(e=>legal.Contains(e.Action)).ToArray();
                    var candidates=ranked.Take(width).Concat(ranked.GroupBy(e=>e.Action.Kind).Select(g=>g.First())).Distinct().ToArray();
                    if(candidates.Length==0){terminal=node.Value;break;}
                    var edge=candidates.MaxBy(e=>(e.Visits>0?e.Total/e.Visits:node.Value)+1.5*e.Prior*Math.Sqrt(node.Visits+1)/(1+e.Visits)-e.Pending)!;
                    edge.Pending++;path.Add(edge);node.Visits++;
                    var battleSeed=random.Next();var result=env.Step(edge.Action,b=>Opponent(b.Day,random.Next()),battleSeed);
                    memory.Applied(edge.Action,env.State,result.Battle is not null);decisions=result.Battle is null?decisions+1:0;
                    if(result.Terminal){terminal=result.Reward;break;}
                    var o=env.GetObservation();var key=Key(o,decisions,memory);
                    if(!visited.Add(key)){terminal=node.Value;break;}
                    if(!edge.Children.TryGetValue(key,out var child)){child=new();edge.Children.Add(key,child);Expand(child,o,Legal(env,memory,decisions),decisions,memory);}
                    node=child;
                    if(!node.Evaluated){pending.Add((node,o,env.State.Day>=observation.Day+options.HorizonDays?Analysis(o):new float[28]));break;}
                    if(env.State.Day>=observation.Day+options.HorizonDays)break;
                }
                paths.Add((path,node,terminal));
            }
            if(paths.Count==0)break;
            var unique=pending.DistinctBy(x=>x.Node).ToArray();if(unique.Length>0)Predict(unique,false);
            foreach(var path in paths){var value=path.Terminal??path.Leaf.Value;
                foreach(var edge in path.Edges){edge.Pending--;edge.Visits++;edge.Total+=value;}
                simulations++;
            }
        }
        var total=root.Edges.Sum(e=>e.Visits);var alternatives=root.Edges.Select(e=>new SearchAlternative(e.Action,e.Visits,e.Visits>0?e.Total/e.Visits:root.Value,total>0?(double)e.Visits/total:e.Prior)).ToArray();
        var selected=alternatives.OrderByDescending(e=>e.Visits).ThenByDescending(e=>e.Value).ThenByDescending(e=>root.Edges.First(x=>x.Action==e.Action).Prior).First();
        _previousRoot=root;
        var continuation=new List<SearchContinuation>{new(PositionIdentity(observation),selected.Action)};var following=root.Edges.First(e=>e.Action==selected.Action);
        for(var depth=0;depth<15;depth++){
            var child=following.Children.Values.OrderByDescending(n=>n.Visits).FirstOrDefault();
            if(child is null||child.Edges.All(e=>e.Visits==0))break;
            following=child.Edges.OrderByDescending(e=>e.Visits).ThenByDescending(e=>e.Total/Math.Max(1,e.Visits)).First();
            continuation.Add(new(child.Position,following.Action));
        }
        return new(1,selected.Action,alternatives,simulations,watch.Elapsed.TotalSeconds,watch.Elapsed.TotalSeconds>=options.Seconds,_encoding.Observation(observation,shopDecisions,64,rootMemory),root.Edges.Select(e=>_encoding.Action(observation,e.Action)).ToArray(),rootFeatures,
            alternatives.Select(x=>x.Probability).ToArray(),guide.Opportunities(observation),guide.Warnings,continuation.ToArray());

        FrozenBoard Opponent(int day,ulong seed){if(opponentPicker is not null)return opponentPicker(day,seed);var rng=new SeedStream(seed);var pool=opponents.Where(x=>x.Board.Day==day).ToArray();
            if(pool.Length==0)throw new InvalidOperationException("No independent opponent available for day "+day);
            var users=pool.Where(x=>x.Kind=="user").GroupBy(x=>x.SessionId).ToArray();
            if(users.Length>0&&rng.Uniform()<Math.Min(.3,.1*users.Length)){var session=users[rng.Index(users.Length)].ToArray();return session[rng.Index(session.Length)].Board;}
            var others=pool.Where(x=>x.Kind!="user").ToArray();if(others.Length==0)throw new InvalidOperationException("A non-user opponent pool is required.");
            return others[rng.Index(others.Length)].Board;}
        float[] Analysis(Observation o){
            cancellation.ThrowIfCancellationRequested();var board=StateSnapshots.Board(o);var key=JsonSerializer.Serialize(board);
            if(!analyses.TryGetValue(key,out var features)){features=_analyzer.Analyze(board,samples:options.AnalysisSamples,seed:options.Seed,cancellation:cancellation).Features;analyses[key]=features;}return features;
        }
        void Expand(Node node,Observation o,SimAction[] legal,int count,ShopTurnMemory turn){node.Edges=legal.Select(a=>new Edge(a,guide.Prior(o,a))).ToArray();node.Encoded=_encoding.Observation(o,count,64,turn);node.Position=PositionIdentity(o);}
        void Predict((Node Node,Observation Observation,float[] Analysis)[] leaves,bool rootPrediction){
            var requests=leaves.Select(l=>new SearchEvaluation(l.Node.Encoded,l.Node.Edges.Select(e=>_encoding.Action(l.Observation,e.Action)).ToArray(),l.Analysis,l.Node.Edges.Select(e=>e.Guide).ToArray())).ToArray();
            var predictions=evaluate?.Invoke(requests);
            if(predictions is not null&&predictions.Length!=leaves.Length)throw new InvalidDataException("Search prediction count differs.");
            for(var i=0;i<leaves.Length;i++){
                var node=leaves[i].Node;var guideLogits=node.Edges.Select(e=>e.Guide).ToArray();var max=guideLogits.DefaultIfEmpty(0).Max();var guideProb=guideLogits.Select(x=>Math.Exp(Math.Clamp(x-max,-20,0))).ToArray();var sum=guideProb.Sum();
                var prediction=predictions?[i];
                if(prediction is not null&&(prediction.Priors.Length!=node.Edges.Length||!double.IsFinite(prediction.Value)||prediction.Priors.Any(p=>!double.IsFinite(p)||p<0)))throw new InvalidDataException("Invalid search prediction.");
                var neuralSum=prediction?.Priors.Sum()??0;
                for(var j=0;j<node.Edges.Length;j++)node.Edges[j].Prior=neuralSum>0?.75*prediction!.Priors[j]/neuralSum+.25*guideProb[j]/sum:guideProb[j]/Math.Max(1e-8,sum);
                node.Value=prediction is null?Bootstrap(leaves[i].Observation):Math.Clamp(prediction.Value,0,1);node.Evaluated=true;
            }
        }
        double Bootstrap(Observation o){
            // Cold-start rollouts reach real game outcomes; this is not a DPS reward.
            var rollout=new GameEnvironment(rules);rollout.Restore(VisibleState(o,random.Next()));var policy=new StrongestShopPolicy(rules);var count=0;
            for(var step=0;step<1800&&rollout.State.Phase!=Phase.Complete;step++){
                cancellation.ThrowIfCancellationRequested();var action=rollout.State.Phase==Phase.Shop&&count>=60?new SimAction(Move.Finish):policy.Choose(rollout);
                var result=rollout.Step(action,b=>Opponent(b.Day,random.Next()),random.Next());count=result.Battle is null?count+1:0;
            }
            return rollout.State.Wins>=rules.Parameters.WinTarget?1:0;
        }
    }
    private static SimAction[] Legal(GameEnvironment env,ShopTurnMemory memory,int decisions)=>memory.Filter(env.State,TrainingObjective.Candidates(env,decisions,64));
    private static string Key(Observation o,int decisions,ShopTurnMemory memory)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {o,decisions,memory.Visited,memory.LastAction,memory.RepeatedKind})));
    private sealed class Node {public Edge[] Edges=[];public string Position="";public float[] Encoded=[];public int Visits;public double Value=.5;public bool Evaluated;}
    private sealed class Edge(SimAction action,double guide){public SimAction Action=action;public double Guide=guide,Prior,Total;public int Visits,Pending;public Dictionary<string,Node> Children=[];}
}
