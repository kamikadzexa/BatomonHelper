using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace BatomonHelper.Training;

public partial class MainWindow
{
    private CancellationTokenSource? _adviceCancellation;
    private SimAction? _recommendedAction;
    private SearchContinuation[] _continuation=[];
    private string? _adviceSession;
    private int _adviceDay;
    private double _thinkingRemaining=30;
    private string _playAdvice="Plan a shop to compare purchases, positioning and build opportunities.";
    public string PlayAdvice {get=>_playAdvice;set=>Set(ref _playAdvice,value);}
    private async void RecommendPlay(object sender,RoutedEventArgs e)
    {
        if(_play is null||_playBusy||_play.Environment.State.Phase==Phase.Complete)return;
        if(_adviceSession!=_play.Session.Id||_adviceDay!=_play.Environment.State.Day){_adviceSession=_play.Session.Id;_adviceDay=_play.Environment.State.Day;_thinkingRemaining=30;}
        if(_thinkingRemaining<=.01){PlayAdvice="This shop's 30-second thinking budget is used. Apply the recommendation or continue manually.";return;}
        _adviceCancellation?.Cancel();var cancellation=new CancellationTokenSource();_adviceCancellation=cancellation;_recommendedAction=null;
        var session=_play;var observation=session.Environment.GetObservation();var budget=Math.Min(10,_thinkingRemaining);PlayAdvice=$"Planning shop · {budget:0.#} seconds for this search, {_thinkingRemaining:0.#} seconds remaining this shop…";
        try{
            SearchResult result;var settings=Path.Combine(CurrentOutput,"configuration.json");
            if(File.Exists(Path.Combine(CurrentOutput,"strategy-checkpoint.pt"))&&File.Exists(settings)){
                var directory=Path.Combine(_root,"work","advisor",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                var request=Path.Combine(directory,"position.json");var response=Path.Combine(directory,"result.json");
                await File.WriteAllTextAsync(request,JsonSerializer.Serialize(observation),cancellation.Token);
                var start=new ProcessStartInfo(Path.Combine(_root,"training",".venv","Scripts","python.exe")) {WorkingDirectory=_root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{Path.Combine(_root,"training","strategy.py"),"--config",settings,"--advise",request,"--result",response,"--thinking-seconds",budget.ToString(System.Globalization.CultureInfo.InvariantCulture)})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)!;using var registration=cancellation.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
                var errors=process.StandardError.ReadToEndAsync();var stdout=process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(cancellation.Token);await stdout;
                if(process.ExitCode!=0)throw new InvalidOperationException(await errors);
                result=JsonSerializer.Deserialize<SearchResult>(await File.ReadAllTextAsync(response,cancellation.Token))!;
            }else{
                var knowledgePath=Path.Combine(_root,"assets","simulation","strategy-knowledge.json");
                var knowledge=File.Exists(knowledgePath)?JsonSerializer.Deserialize<StrategyKnowledge>(await File.ReadAllTextAsync(knowledgePath,cancellation.Token)):null;
                var guide=new StrategyKnowledgeGuide(_rules,knowledge);
                result=await Task.Run(()=>{
                    var pool=IndependentOpponentArchive.Build(_rules,1000000076);var scripted=GuidedShopPolicy.Archive(_rules,guide,2000000080,pool);
                    return new StrategySearch(_rules,guide).Plan(observation,[..pool,..scripted],new(Seconds:budget,Simulations:100000,AnalysisSamples:4),cancellation:cancellation.Token);
                },cancellation.Token);
            }
            if(cancellation.IsCancellationRequested||!ReferenceEquals(session,_play))return;
            _thinkingRemaining=Math.Max(0,budget-result.Seconds);
            _recommendedAction=result.Action;
            _continuation=result.Continuation??[];
            string Describe(SimAction a)=>$"{a.Kind}"+(a.Source>=0?$" from slot {a.Source+1}":"")+(a.Target>=0?$" to slot {a.Target+1}":"")+(a.Choice>=0?$" choice {a.Choice+1}":"");
            PlayAdvice=$"Recommended: {Describe(result.Action)}\n{result.Simulations} simulations · {result.Seconds:0.#}s · "+
                (File.Exists(Path.Combine(CurrentOutput,"strategy-checkpoint.pt"))?"learned policy + search":"scripted rollout search (no strategic model loaded)")+"\n"+
                string.Join("\n",result.Alternatives.OrderByDescending(x=>x.Visits).Take(4).Select(a=>$"{Describe(a.Action)}: {a.Visits} visits · estimated run success {a.Value:P0}"))+"\n"+
                string.Join("\n",result.Opportunities.Take(4).Select(o=>$"{o.Name}: missing {string.Join(", ",o.Missing)}"))+"\n"+string.Join("\n",result.Limitations);
        }catch(OperationCanceledException){if(ReferenceEquals(_adviceCancellation,cancellation))PlayAdvice="Planning cancelled.";}
        catch(Exception error){if(!cancellation.IsCancellationRequested)PlayAdvice="Planning failed: "+error.Message;}
        finally{if(ReferenceEquals(_adviceCancellation,cancellation))_adviceCancellation=null;cancellation.Dispose();}
    }
    private void CancelAdvice(object sender,RoutedEventArgs e)=>_adviceCancellation?.Cancel();
    private async void ApplyRecommendation(object sender,RoutedEventArgs e){
        if(_recommendedAction is not {} action||_play?.Environment.GetLegalActions().Contains(action)!=true)return;
        var remaining=_continuation.Skip(1).ToArray();await ApplyPlayAction(action);
        var identity=StrategySearch.PositionIdentity(_play.Environment.GetObservation());
        if(remaining.FirstOrDefault() is {} next&&next.ObservationIdentity==identity&&_play.Environment.GetLegalActions().Contains(next.Action)){
            _continuation=remaining;_recommendedAction=next.Action;
            PlayAdvice=$"Continue validated plan: {next.Action.Kind} · source {next.Action.Source+1}, target {next.Action.Target+1}. {_thinkingRemaining:0.#}s search budget remains.";
        }else{_continuation=[];PlayAdvice="Board changed. Replan to account for the revealed outcome, or continue manually.";}
    }
}
