using BatomonHelper.Simulation;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BatomonHelper.Training;

public sealed record DamageRow(string Time,string Dps,string Damage,string Direct,string Burn,string Poison,string Shock,string Stacks,string Units);
public sealed record AnalysisContext(MainWindow Owner);
public partial class MainWindow
{
    public AnalysisContext AnalysisView=>new(this);
    private CancellationTokenSource? _analysisCancellation;
    private BoardAnalyzer? _analyzer;private RulesManifest? _analysisRules;
    private FrozenBoard? _analysisBoard;
    private string _damageSummary="Select a board to analyze.",_damageLimits="",_targetHp="";
    private PointCollection _damageCurve=[],_hpLine=[];
    public string DamageSummary {get=>_damageSummary;set=>Set(ref _damageSummary,value);}
    public string DamageLimits {get=>_damageLimits;set=>Set(ref _damageLimits,value);}
    public string TargetHp {get=>_targetHp;set=>Set(ref _targetHp,value);}
    public PointCollection DamageCurve {get=>_damageCurve;set=>Set(ref _damageCurve,value);}
    public PointCollection HpLine {get=>_hpLine;set=>Set(ref _hpLine,value);}
    public ObservableCollection<DamageRow> DamageRows {get;}=[];
    private async void AnalyzeBoard(FrozenBoard board,RulesManifest rules)
    {
        _analysisCancellation?.Cancel();var cancellation=new CancellationTokenSource();_analysisCancellation=cancellation;
        _analysisBoard=StateSnapshots.Copy(board);
        if(!ReferenceEquals(_analysisRules,rules)){_analysisRules=rules;_analyzer=new(rules);}
        var analyzer=_analyzer!;double? hp=null;
        if(!string.IsNullOrWhiteSpace(TargetHp)){
            if(!double.TryParse(TargetHp,NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)||!double.IsFinite(parsed)||parsed<=0){DamageSummary="Target HP must be a positive number.";DamageRows.Clear();DamageCurve=[];HpLine=[];cancellation.Dispose();_analysisCancellation=null;return;}hp=parsed;
        }
        DamageSummary="Analyzing 32 combat samples…";DamageRows.Clear();DamageCurve=[];HpLine=[];
        try{
            var result=await Task.Run(()=>analyzer.Analyze(board,hp,32,cancellation:cancellation.Token));
            if(cancellation.IsCancellationRequested)return;
            ShowAnalysis(result);
        }catch(OperationCanceledException){}catch(Exception error){if(!cancellation.IsCancellationRequested)DamageSummary="Analysis failed: "+error.Message;}
        finally{cancellation.Dispose();if(ReferenceEquals(_analysisCancellation,cancellation))_analysisCancellation=null;}
    }
    private void ShowAnalysis(BoardAnalysisResult result)
    {
            DamageRows.Clear();
            DamageSummary=$"Passive-target damage · {result.Samples} samples · target {result.TargetHp:N0} HP ({result.TargetProvenance})\n"+
                (result.MedianKillSeconds is {} seconds?$"Estimated win at {seconds:0.0}s ({seconds / 30:0%}), median among successful samples · reach rate {result.ReachProbability:P0}":"Target not defeated within 30 seconds")+
                $"\nDamage at 30s: {result.DamagePercentAt30:0.#}% of target HP. DPS = average damage per second since battle start.";
            foreach(var point in result.Checkpoints){
                var units=string.Join("\n",point.Units.Select(u=>$"Slot {u.Slot+1} {u.Species}{(u.Alive?"":" (knocked out)")}: "+
                    string.Join(" · ",Enumerable.Range(0,8).Select(i=>$"{(Stat)i} {u.Stats[i]:0.##}"))+$" · cooldown {u.Cooldown:0.##}s\n"+
                    string.Join(" · ",u.Counters.Select(x=>$"{x.Key}: {x.Value:0.##}"))));
                DamageRows.Add(new($"{point.Seconds:0.#}s",point.Dps.ToString("N1"),$"{point.Damage:N1} [range {point.Minimum:N1}–{point.Maximum:N1}; σ {point.StandardDeviation:N1}]",
                    point.DamageByKind["Direct"].ToString("N1"),point.DamageByKind["Burn"].ToString("N1"),point.DamageByKind["Poison"].ToString("N1"),point.DamageByKind["Shock"].ToString("N1"),
                    $"Burn {point.StatusStacks[0]:0.#} · Poison {point.StatusStacks[1]:0.#} · Shock {point.StatusStacks[2]:0.#}",units));
            }
            var max=Math.Max(result.TargetHp,result.Curve.Max(x=>x.Damage))*1.05;
            DamageCurve=new(result.Curve.Select(p=>new Point(p.Seconds/30*600,150-p.Damage/max*145)));
            HpLine=new([new Point(0,150-result.TargetHp/max*145),new Point(600,150-result.TargetHp/max*145)]);
            DamageLimits=string.Join("\n",result.Limitations);
    }
    public void RenderAnalysisPreview(string path){
        var rules=RuleCompiler.Fixture();var board=new FrozenBoard(1,300,"fixture",[new(){SpeciesId=rules.Species[0].Id},new(){SpeciesId=rules.Species[1].Id},null,null,null,null],[],[]);
        ShowAnalysis(new BoardAnalyzer(rules).Analyze(board,300,32));Status="Synthetic fixture · damage analysis layout verification";Tabs.SelectedIndex=3;RenderWindow(path);
    }
    private void ReanalyzeBoard(object sender,RoutedEventArgs e){if(_analysisBoard is {} board && _analysisRules is {} rules)AnalyzeBoard(board,rules);}
}
