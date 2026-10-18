using System.Security.Cryptography;
using System.Text.Json;

namespace BatomonHelper.Simulation;

public sealed record DamagePacket(double Seconds,string Kind,double Amount);
public sealed record CombatUnitSnapshot(int Slot,string Species,bool Alive,double[] Stats,double? Cooldown,Dictionary<string,double> Counters);
public sealed record CombatFrame(double Seconds,double[] EnemyStatuses,CombatUnitSnapshot[] Units)
{
    public Dictionary<string,double> OutputRates {get;init;}=[];
}
/// <summary>Optional observer; normal combat never uses the analysis horizon or ignores defeat.</summary>
public sealed class CombatProbe
{
    public double Duration {get;init;}=31;
    public CancellationToken Cancellation {get;init;}
    public List<DamagePacket> Damage {get;}=[];
    public List<DamagePacket> Output {get;}=[];
    public List<CombatFrame> Frames {get;}=[];
}
public sealed record DamagePoint(double Seconds,double Dps,double Damage,double Minimum,double Maximum,double StandardDeviation,
    Dictionary<string,double> DamageByKind,double[] StatusStacks,CombatUnitSnapshot[] Units)
{
    public Dictionary<string,double> OutputPerSecond {get;init;}=[];
    public Dictionary<string,double> OutputTotals {get;init;}=[];
}
public sealed record BoardAnalysisResult(int Version,string RulesHash,string Identity,int Samples,double TargetHp,string TargetProvenance,
    double ReachProbability,double? MedianKillSeconds,double? MedianTimePercent,double DamagePercentAt30,DamagePoint[] Checkpoints,
    DamagePoint[] Curve,string[] Limitations,float[] Features);

/// <summary>Passive-target offensive projection using the production combat resolver, never a second damage formula.</summary>
public sealed class BoardAnalyzer(RulesManifest rules)
{
    public const int Version=3;
    private readonly Dictionary<string,BoardAnalysisResult> _cache=[];
    private readonly Queue<string> _order=[];
    private readonly object _gate=new();
    public BoardAnalysisResult Analyze(FrozenBoard board,double? targetHp=null,int samples=32,ulong seed=69,CancellationToken cancellation=default,bool reference=false)
    {
        if(samples<1||samples>256)throw new ArgumentOutOfRangeException(nameof(samples));
        var hp=targetHp??rules.Parameters.DailyHp[Math.Clamp(board.Day-1,0,rules.Parameters.DailyHp.Length-1)];
        if(!double.IsFinite(hp)||hp<=0)throw new ArgumentOutOfRangeException(nameof(targetHp));
        var provenance=targetHp.HasValue?"Manual target HP":rules.Parameters.HpEvidence?.FirstOrDefault(x=>x.Day==board.Day)?.Kind.ToString()??(rules.Parameters.Verified?"Verified rules HP":"Assumed rules HP");
        var identity=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {Version,rules.Hash,board,hp,samples,seed,reference,provenance})));
        cancellation.ThrowIfCancellationRequested();
        lock(_gate)if(_cache.TryGetValue(identity,out var cached))return cached;
        var probes=new List<CombatProbe>();var kills=new List<double>();
        var passive=new FrozenBoard(board.Day,hp,"__passive_target__",new Unit?[6],[],[]);
        // The synthetic trainer has no effects. It exists only inside this projection.
        var projectionRules=rules with {Trainers=[..rules.Trainers,new("__passive_target__","Passive target",0,false,0,[],"analysis","")]};
        var resolver=new Combat(projectionRules);
        for(var sample=0;sample<samples;sample++){
            cancellation.ThrowIfCancellationRequested();
            var probe=new CombatProbe {Cancellation=cancellation};
            resolver.ResolveBattle(StateSnapshots.Copy(board),passive,seed+(ulong)sample*0x9E3779B97F4A7C15UL,reference:reference,probe:probe);
            probes.Add(probe);double cumulative=0;
            foreach(var group in probe.Damage.Where(x=>x.Seconds<=30+1e-8).GroupBy(x=>x.Seconds).OrderBy(x=>x.Key)){
                cumulative+=group.Sum(x=>x.Amount);if(cumulative>=hp){kills.Add(group.Key);break;}
            }
        }
        DamagePoint Point(double time){
            bool InWindow(double seconds) => time>=30 ? seconds>29+1e-8 && seconds<=30+1e-8 :
                (time==0?seconds>=0:seconds>time+1e-8) && seconds<=time+1+1e-8;
            var totals=probes.Select(p=>p.Damage.Where(x=>x.Seconds<=time+1e-8).Sum(x=>x.Amount)).ToArray();var mean=totals.Average();
            var frames=probes.Select(p=>p.Frames.Last(x=>x.Seconds<=time+1e-8)).ToArray();
            var units=frames.SelectMany(f=>f.Units).GroupBy(x=>x.Slot).OrderBy(x=>x.Key).Select(g=>new CombatUnitSnapshot(g.Key,string.Join(" / ",g.Select(x=>x.Species).Distinct()),g.Any(x=>x.Alive),
                Enumerable.Range(0,9).Select(i=>g.Sum(x=>x.Stats[i])/samples).ToArray(),g.Any(x=>x.Cooldown.HasValue)?g.Where(x=>x.Cooldown.HasValue).Average(x=>x.Cooldown!.Value):null,
                g.SelectMany(x=>x.Counters.Keys).Distinct().ToDictionary(k=>k,k=>g.Sum(x=>x.Counters.GetValueOrDefault(k))/samples))).ToArray();
            return new DamagePoint(time,probes.Average(p=>p.Damage.Where(x=>InWindow(x.Seconds)).Sum(x=>x.Amount)),mean,totals.Min(),totals.Max(),Math.Sqrt(totals.Average(x=>(x-mean)*(x-mean))),
                new[]{"Direct","Burn","Poison","Shock"}.ToDictionary(k=>k,k=>probes.Average(p=>p.Damage.Where(x=>x.Kind==k&&x.Seconds<=time+1e-8).Sum(x=>x.Amount))),
                Enumerable.Range(0,3).Select(i=>frames.Average(x=>x.EnemyStatuses[i])).ToArray(),units)
            {
                OutputPerSecond=new[]{"Burn","Poison","Shock","Heal","Shield"}.ToDictionary(kind=>kind,kind=>frames.Average(frame=>frame.OutputRates.GetValueOrDefault(kind))),
                OutputTotals=new[]{"Burn","Poison","Shock","Heal","Shield"}.ToDictionary(kind=>kind,kind=>probes.Average(p=>p.Output.Where(x=>x.Kind==kind && x.Seconds<=time+1e-8).Sum(x=>x.Amount)))
            };
        }
        var curve=Enumerable.Range(0,61).Select(i=>Point(i*.5)).ToArray();
        var checkpoints=new[]{0d,7.5,15,22.5,30}.Select(Point).ToArray();
        kills.Sort();double? median=kills.Count==0?null:kills.Count%2==1?kills[kills.Count/2]:(kills[kills.Count/2-1]+kills[kills.Count/2])/2;
        var effects=board.Team.OfType<Unit>().SelectMany(u=>LevelRules.Resolve(rules.Species.First(s=>s.Id==u.SpeciesId),u.Level,u.Shiny).Effects)
            .Concat(board.Bag.SelectMany(b=>rules.Trinkets.First(t=>t.Id==b.Id).Effects)).Concat(board.ExtraTrainers.Prepend(board.Trainer).SelectMany(t=>rules.Trainers.First(x=>x.Id==t).Effects)).ToArray();
        var limitations=new List<string>{"Passive HP-only target; no enemy attacks, healing, shields, cleansing or sudden death. Not a win probability."};
        limitations.Add("Effective combat caps are applied: first six stats ≤ 999,999,999; cooldown ≥ 0.1s; multicast ≥ 1 (Zephyrex is fixed at 1).");
        if(effects.Any(e=>e.Target is Target.Opposite or Target.Enemies or Target.RandomEnemy or Target.EnemyRow or Target.BothTeams or Target.BothRows || e.Operation==Operation.CopyEnemyStats))limitations.Add("Enemy-unit-dependent effects have no targets in this projection.");
        if(effects.Any(e=>e.Trigger==Trigger.AnyKnockout))limitations.Add("Only your own unit knockouts can activate knockout-dependent effects here.");
        if(rules.GuessedRules>0)limitations.Add($"Simulator uses {rules.GuessedRules} labelled rules assumptions.");
        var features=checkpoints.SelectMany(p=>new[]{(float)Math.Log(1+p.Dps)/10,(float)Math.Log(1+p.Damage)/10}.Concat(p.StatusStacks.Select(s=>(float)Math.Log(1+s)/10)))
            .Concat(new[]{(float)kills.Count/samples,(float)(median??30)/30,(float)Math.Log(1+checkpoints[^1].Damage/hp)}).ToArray();
        var result=new BoardAnalysisResult(Version,rules.Hash,identity,samples,hp,provenance,(double)kills.Count/samples,median,median/30*100,checkpoints[^1].Damage/hp*100,checkpoints,curve,limitations.ToArray(),features);
        lock(_gate){if(_cache.Count>=128)_cache.Remove(_order.Dequeue());if(!_cache.ContainsKey(identity)){_cache.Add(identity,result);_order.Enqueue(identity);}}
        return result;
    }
}
