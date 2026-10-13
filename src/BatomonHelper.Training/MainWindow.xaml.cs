using BatomonHelper.Simulation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BatomonHelper.Training;

public sealed record HistoryCard(string Name,string Level,string Types,string Stats,string? Sprite,
    int Slot=-1,bool IsOccupied=false,string Zone="");
public sealed record GenerationResult(int Generation,int Runs,double SuccessRate,double AverageWins);
public sealed record TrainerResult(string Trainer,int Runs,double SuccessRate,double AverageWins);
public sealed record RankingResult(int Rank,string Label,int Update,int Generation,int Runs,double SuccessRate,double Lower95,double Upper95,double AverageWins,double AverageDeployed,double UnusedBench,double LimitedTurns,string? Archive,bool Complete,bool Baseline);
public partial class MainWindow : Window,INotifyPropertyChanged
{
    private readonly string _root;
    private RulesManifest _rules;
    private RulesManifest? _historyRules;
    private Process? _process;
    private HistoryStore? _history;
    private string? _historyDirectory;
    private RunRecord? _selected;
    private int _day;
    private bool _refreshing,_closing;
    private string _status="Loading rules…",_progress="No training session yet.",_checkpoint="",_selectedRunLabel="Select a run to inspect its day-by-day boards.",_dayLabel="",_inventory="",_decisions="";
    private PointCollection _curve=[];
    public string OutputDirectory {get;}
    public string RulesLabel => $"{_rules.Version} · build {_rules.Build} · {_rules.Issues.Length} missing handlers · {_rules.GuessedRules} guesses";
    public string Status {get=>_status;set=>Set(ref _status,value);}
    public string Progress {get=>_progress;set=>Set(ref _progress,value);}
    public string Checkpoint {get=>_checkpoint;set=>Set(ref _checkpoint,value);}
    public string SelectedRunLabel {get=>_selectedRunLabel;set=>Set(ref _selectedRunLabel,value);}
    public string DayLabel {get=>_dayLabel;set=>Set(ref _dayLabel,value);}
    public string Inventory {get=>_inventory;set=>Set(ref _inventory,value);}
    public string Decisions {get=>_decisions;set=>Set(ref _decisions,value);}
    public PointCollection Curve {get=>_curve;set=>Set(ref _curve,value);}
    public ObservableCollection<RuleIssue> Issues {get;}=[];
    public ObservableCollection<RuleEvidence> Evidence {get;}=[];
    public ObservableCollection<ValueEvidence> HpEvidence {get;}=[];
    public ObservableCollection<ValueEvidence> IncomeEvidence {get;}=[];
    public ObservableCollection<RunSummary> Runs {get;}=[];
    public ObservableCollection<HistoryCard> Team {get;}=[];
    public ObservableCollection<HistoryCard> Bench {get;}=[];
    public ObservableCollection<TrainerResult> TrainerResults {get;}=[];
    public ObservableCollection<GenerationResult> GenerationResults {get;}=[];
    public ObservableCollection<RankingResult> RankingResults {get;}=[];
    public event PropertyChangedEventHandler? PropertyChanged;
    public MainWindow()
    {
        _root=FindRoot();
        var path=Path.Combine(_root,"assets","simulation","rules.json");
        _rules=File.Exists(path)?JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(path))!:RuleCompiler.Fixture() with {Issues=[new("rules-file","Source","Refresh the simulation rules first.","https://batomon.com/status")]};
        OutputDirectory=Path.Combine(_root,"work","training");
        InitializeComponent(); DataContext=this;InitializePlay();
        LoadConfiguration(OutputDirectory);
        foreach(var issue in _rules.Issues) Issues.Add(issue);
        foreach(var entry in _rules.Evidence??[])Evidence.Add(entry);
        foreach(var entry in _rules.Parameters.HpEvidence??[])HpEvidence.Add(entry);
        foreach(var entry in _rules.Parameters.IncomeEvidence??[])IncomeEvidence.Add(entry);
        Status=_rules.Ready?(_rules.FullFidelityReady?"Ready for standalone Pantra training.":$"Ready with {_rules.GuessedRules} explicitly guessed rules."):
            $"Training blocked by {_rules.Issues.Length} missing handlers. {_rules.GuessedRules} labelled guesses are authorized. Review Rules coverage.";
        var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)}; timer.Tick+=async(_,_)=>await UpdateProgress(); timer.Start();
        Closing+=async(_,e)=>
        {
            if(_closing) return;
            if(_process is {HasExited:false})
            {
                e.Cancel=true; _closing=true; Control("stop"); Status="Saving checkpoint before closing…";
                await _process.WaitForExitAsync(); if(_history is not null) await _history.DisposeAsync(); Close();
            }
            else if(_history is not null) { _closing=true; await _history.DisposeAsync(); }
        };
    }
    public void OpenOutput(string directory)
    {
        Output.Text=Path.GetFullPath(directory);
        LoadConfiguration(CurrentOutput);
        Tabs.SelectedIndex=1;RefreshHistory(this,new RoutedEventArgs());
    }
    private void LoadConfiguration(string directory)
    {
        var configuration=Path.Combine(directory,"configuration.json");
        if(File.Exists(configuration)){
            using var json=JsonDocument.Parse(File.ReadAllText(configuration));var c=json.RootElement;
            Seed.Text=c.GetProperty("seed").ToString();Workers.Text=c.GetProperty("workers").ToString();
            Environments.Text=(c.TryGetProperty("requested_environments",out var requested)?requested:c.GetProperty("environments")).ToString();
            Minibatch.Text=c.TryGetProperty("minibatch",out var batch)?batch.ToString():"512";
            AutoCalibrate.IsChecked=c.TryGetProperty("auto_calibrate",out var calibrate)&&calibrate.GetBoolean();
            Device.SelectedIndex=c.GetProperty("device").GetString() switch {"cpu"=>1,"cuda"=>2,_=>0};
        }
    }
    private static string FindRoot()
    {
        foreach(var start in new[]{Environment.CurrentDirectory,AppContext.BaseDirectory})
            for(var d=new DirectoryInfo(start);d is not null;d=d.Parent)
                if(File.Exists(Path.Combine(d.FullName,"BatomonHelper.sln"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    private void Set<T>(ref T field,T value,[CallerMemberName]string? name=null) { field=value; PropertyChanged?.Invoke(this,new(name)); }
    private string CurrentOutput => Path.GetFullPath(Output.Text);
    private void Control(string command)
    {
        Directory.CreateDirectory(CurrentOutput); Atomic(Path.Combine(CurrentOutput,"control.json"),JsonSerializer.Serialize(new {command}));
    }
    private static void Atomic(string path,string value) { File.WriteAllText(path+".ui.tmp",value); File.Move(path+".ui.tmp",path,true); }
    private async void Start(object sender,RoutedEventArgs e) => await Launch(false);
    private async Task Launch(bool resume)
    {
        try
        {
            if(_process is {HasExited:false}) { Status="A training process is already running."; return; }
            var rulesPath=Path.Combine(_root,"assets","simulation","rules.json");
            if(Diagnostic.IsChecked==true)
            {
                _rules=RuleCompiler.Fixture(); rulesPath=Path.Combine(CurrentOutput,"diagnostic-rules.json");
                Directory.CreateDirectory(CurrentOutput); File.WriteAllText(rulesPath,JsonSerializer.Serialize(_rules));
            }
            else { _rules=JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(rulesPath))!; _rules.RequireReady(); }
            var config=new Dictionary<string,object> { ["output"]=CurrentOutput,["rules"]=rulesPath,
                ["host"]=Path.Combine(_root,"tools","BatomonHelper.TrainingHost","bin","Release","net10.0","BatomonHelper.TrainingHost.dll"),
                ["workers"]=int.Parse(Workers.Text),["environments"]=int.Parse(Environments.Text),["seed"]=int.Parse(Seed.Text),
                ["minutes"]=double.Parse(Minutes.Text,System.Globalization.CultureInfo.InvariantCulture),["device"]=((ComboBoxItem)Device.SelectedItem).Content.ToString()!,
                ["rollout"]=128,["user_pool"]=Path.Combine(_root,"work","user-sessions"),["comparison_archives"]=Directory.Exists(Path.Combine(_root,"work","archives"))?Directory.GetDirectories(Path.Combine(_root,"work","archives")).Where(x=>File.Exists(Path.Combine(x,"checkpoint.json"))).ToArray():Array.Empty<string>(),["minibatch"]=int.Parse(Minibatch.Text),["updates"]=0,["resume"]=resume,["auto_calibrate"]=AutoCalibrate.IsChecked==true&&!resume };
            if((int)config["environments"]<4 || (int)config["workers"]<1) throw new ArgumentException("Use at least four environments and one worker.");
            if((int)config["minibatch"]<32)throw new ArgumentException("Use a PPO batch of at least 32.");
            Directory.CreateDirectory(CurrentOutput); Control("run"); Atomic(Path.Combine(CurrentOutput,"launch.json"),JsonSerializer.Serialize(config));
            StartPython("--config",Path.Combine(CurrentOutput,"launch.json"));
            Status=Diagnostic.IsChecked==true?"Synthetic diagnostic training — results are not Batomon performance.":$"Starting Pantra training with {_rules.GuessedRules} labelled guesses…";
            await Task.CompletedTask;
        }
        catch(Exception error) { Status=error.Message; }
    }
    private void StartPython(params string[] arguments)
    {
        Control("run");
        var python=Path.Combine(_root,"training",".venv","Scripts","python.exe");
        if(!File.Exists(python)) throw new FileNotFoundException("Run training/Setup.ps1 to install the isolated learner.");
        var start=new ProcessStartInfo(python) {WorkingDirectory=_root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        start.ArgumentList.Add(Path.Combine(_root,"training","learner.py")); foreach(var arg in arguments) start.ArgumentList.Add(arg);
        _process=Process.Start(start)!;
        SessionFields(false);
        var launched=_process;var processOutput=CurrentOutput;launched.EnableRaisingEvents=true;
        launched.Exited+=(_,_)=>Dispatcher.InvokeAsync(()=> {if(ReferenceEquals(_process,launched)){SessionFields(true);
            if(launched.ExitCode!=0){
                var progressPath=Path.Combine(processOutput,"progress.json");
                try{using var failure=JsonDocument.Parse(File.ReadAllText(progressPath));
                    if(failure.RootElement.TryGetProperty("error",out var detail)){Status=detail.GetString()!;return;}}
                catch(Exception error) when(error is IOException or JsonException){ }
                Status=$"Process failed (exit {launched.ExitCode}).";Atomic(progressPath,JsonSerializer.Serialize(new {status="Failed",error=Status}));
            }
            else if(Status.StartsWith("Ranking")||Status.StartsWith("Evaluating"))Status="Process completed.";
        }});
        _process.BeginOutputReadLine(); _process.ErrorDataReceived+=(_,e)=> {if(e.Data is { } message) Dispatcher.InvokeAsync(()=>Status=message);}; _process.BeginErrorReadLine();
    }
    private void SessionFields(bool enabled)
    {
        foreach(var control in new Control[]{Device,Workers,Environments,Minibatch,Seed,Minutes,Output,Diagnostic,AutoCalibrate})control.IsEnabled=enabled;
    }
    private void Pause(object sender,RoutedEventArgs e) { if(_process is {HasExited:false}) {Control("pause");Status="Pausing after the current PPO update…";} }
    private async void Resume(object sender,RoutedEventArgs e)
    {
        if(_process is {HasExited:false}) {Control("run");Status="Resuming training…";}
        else await Launch(true);
    }
    private void Stop(object sender,RoutedEventArgs e) { if(_process is {HasExited:false}) {Control("stop");Status="Stopping after the current PPO update and saving…";} }
    private void Evaluate(object sender,RoutedEventArgs e)
    {
        try { if(_process is {HasExited:false}) throw new InvalidOperationException("Stop and save before evaluation."); StartPython("--config",Path.Combine(CurrentOutput,"configuration.json"),"--evaluate","--runs",ThoroughEvaluation.IsChecked==true?"512":"128"); Status="Evaluating frozen policies on separate seeds…"; }
        catch(Exception error) {Status=error.Message;}
    }
    private void Export(object sender,RoutedEventArgs e)
    {
        try { if(_process is {HasExited:false}) throw new InvalidOperationException("Stop and save before export."); StartPython("--config",Path.Combine(CurrentOutput,"configuration.json"),"--export"); Status="Exporting learned policies and encoding metadata…"; }
        catch(Exception error) {Status=error.Message;}
    }
    private void RankGenerations(object sender,RoutedEventArgs e)
    {
        try {if(_process is {HasExited:false})throw new InvalidOperationException("Stop and save before ranking generations.");StartPython("--config",Path.Combine(CurrentOutput,"configuration.json"),"--rank-generations","--runs",ThoroughEvaluation.IsChecked==true?"512":"128");Status="Ranking every retained generation against one frozen opponent schedule…";}
        catch(Exception error){Status=error.Message;}
    }
    private void ExportSelected(object sender,RoutedEventArgs e)
    {
        try
        {
            if(_process is {HasExited:false})throw new InvalidOperationException("Stop and save before export.");
            if(RankingList.SelectedItem is not RankingResult selected)throw new InvalidOperationException("Select a generation leaderboard row first.");
            if(!selected.Complete||selected.Baseline)throw new InvalidOperationException("Select a completed policy evaluation to export.");
            var arguments=new List<string>{"--config",Path.Combine(CurrentOutput,"configuration.json"),"--export","--export-update",selected.Update.ToString()};
            if(selected.Archive is not null){arguments.Add("--export-archive");arguments.Add(selected.Archive);}
            StartPython(arguments.ToArray());Status=$"Exporting {selected.Label}…";
        }
        catch(Exception error){Status=error.Message;}
    }
    private async Task UpdateProgress()
    {
        if(_refreshing) return; _refreshing=true;
        try
        {
            var path=Path.Combine(CurrentOutput,"progress.json"); if(!File.Exists(path)) return;
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(path)); var p=json.RootElement;
            var status=p.GetProperty("status").GetString();
            if(status=="Failed") {Status=p.GetProperty("error").GetString()!;return;}
            if(status is "Ranking generations" or "Ranked" or "Cancelled" or "Incomplete"){
                LoadRanking();Status=status=="Ranked"?"Generation ranking complete.":status!;
                if(status!="Ranking generations"){
                    Progress=p.TryGetProperty("snapshots",out _)?$"{status} · completed snapshots {NumberOrZero(p,"completed")} / {NumberOrZero(p,"total")}":
                        $"{status} · {NumberOrZero(p,"runs")} / {NumberOrZero(p,"target")} runs";return;
                }
            }
            double Number(string key)=>p.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetDouble():0;
            if(status is "Ranking generations" or "Evaluating" or "Preparing evaluation"){
                var snapshot=p.TryGetProperty("snapshot",out var label)?label.GetString():"Preparing frozen suite";
                var eta=p.TryGetProperty("remaining_seconds",out var remaining)&&remaining.ValueKind==JsonValueKind.Number?$" · ~{remaining.GetDouble():N0}s remaining":"";
                Progress=$"{status} · {snapshot} · {Number("runs"):N0}/{Number("target"):N0} runs\n"+
                    $"Snapshots {Number("completed"):N0}/{Number("total"):N0} · {Number("elapsed_seconds"):N0}s elapsed · {Number("runs_per_second"):F2} runs/s{eta}";
                return;
            }
            Progress=$"{status} · update {Number("update"):N0} · {Number("runs"):N0} completed runs\n"+
                $"Recent ({Number("recent_runs"):N0}) success {Number("recent_success_rate"):P1} · wins {Number("recent_average_wins"):F2} · {Number("decisions_per_second"):N0} decisions/s";
            if(p.TryGetProperty("device",out var device) && p.TryGetProperty("actual_workers",out _))Progress+=$"\nDevice {device.GetString()} · workers {Number("actual_workers"):N0} · environments {Number("actual_environments"):N0}";
            if(p.TryGetProperty("episode_starts",out var starts))Progress+=$"\nEpisode starts: full {starts[0].GetInt64():N0} · middle {starts[1].GetInt64():N0} · late {starts[2].GetInt64():N0}";
            if(p.TryGetProperty("average_deployed",out _))Progress+=$"\nDeployed {Number("average_deployed"):F2}/6 · unused bench {Number("unused_deployable_bench"):F2} · turn limit {Number("limited_turn_rate"):P1}";
            if(p.TryGetProperty("rollout_seconds",out _))Progress+=$"\nSimulation/inference {Number("rollout_seconds"):F2}s · PPO {Number("optimization_seconds"):F2}s per update";
            if(p.TryGetProperty("finish_with_deployable_bench",out _))Progress+=$"\nFinish with usable bench {Number("finish_with_deployable_bench"):N0} · improving moves {Number("deployment_improving"):N0} · reversing moves {Number("deployment_reversing"):N0}";
            if(Number("session_runs")>0)Progress+=$"\nThis launch: {Number("session_runs"):N0} runs · success {Number("session_success_rate"):P1} · average wins {Number("session_average_wins"):F2}";
            Checkpoint=p.TryGetProperty("checkpoint",out var cp)?"Checkpoint: "+cp.ToString():"No checkpoint yet.";
            if(p.TryGetProperty("diagnostic_fixture",out var diagnostic) && diagnostic.GetBoolean()) Status="Synthetic diagnostic fixture — not game training.";
            else Status=status??"Training";
            TrainerResults.Clear(); if(p.TryGetProperty("trainers",out var trainers)) foreach(var t in trainers.EnumerateObject())
            {var n=t.Value.GetProperty("runs").GetInt32();TrainerResults.Add(new(t.Name,n,t.Value.GetProperty("successes").GetInt32()/(double)Math.Max(1,n),t.Value.GetProperty("wins").GetInt32()/(double)Math.Max(1,n)));}
            GenerationResults.Clear();if(p.TryGetProperty("generations",out var cohorts))foreach(var cohort in cohorts.EnumerateObject().OrderBy(x=>int.Parse(x.Name))){
                var n=cohort.Value.GetProperty("runs").GetInt32();GenerationResults.Add(new(int.Parse(cohort.Name),n,cohort.Value.GetProperty("successes").GetDouble()/Math.Max(1,n),cohort.Value.GetProperty("wins").GetDouble()/Math.Max(1,n)));}
            var metricsPath=Path.Combine(CurrentOutput,"metrics.jsonl");
            if(File.Exists(metricsPath))
            {
                var points=new List<Point>(); var index=0;
                foreach(var line in File.ReadLines(metricsPath).TakeLast(200)) {using var entry=JsonDocument.Parse(line);points.Add(new(index++,100*(1-entry.RootElement.GetProperty("success_rate").GetDouble())));}
                if(points.Count==1) points.Add(new(1,points[0].Y)); Curve=new(points);
            }
        }
        catch(Exception error) when(error is IOException or JsonException) {Status="Progress refresh: "+error.Message;}
        finally {_refreshing=false;}
    }
    private static int NumberOrZero(JsonElement p,string key)=>p.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.Number?value.GetInt32():0;
    private void LoadRanking()
    {
        var path=Path.Combine(CurrentOutput,"generation-ranking.json");if(!File.Exists(path))return;
        using var json=JsonDocument.Parse(File.ReadAllText(path));RankingResults.Clear();
        foreach(var row in json.RootElement.GetProperty("snapshots").EnumerateArray())
        {
            var interval=row.GetProperty("confidence_interval_95").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
            RankingResults.Add(new(row.GetProperty("rank").GetInt32(),row.GetProperty("label").GetString()!,row.GetProperty("update").GetInt32(),row.GetProperty("generation").GetInt32(),
                row.GetProperty("runs").GetInt32(),row.GetProperty("success_rate").GetDouble(),interval[0],interval[1],row.GetProperty("average_wins").GetDouble(),
                row.GetProperty("average_deployed").GetDouble(),row.GetProperty("unused_deployable_bench").GetDouble(),row.GetProperty("limited_turn_rate").GetDouble(),row.TryGetProperty("archive",out var archive)?archive.GetString():null,
                row.GetProperty("completed").GetBoolean(),row.TryGetProperty("baseline",out var baseline)&&baseline.GetBoolean()));
        }
    }
    private async void RefreshHistory(object sender,RoutedEventArgs e)
    {
        try
        {
            var directory=Path.Combine(CurrentOutput,"history");
            if(_historyDirectory!=directory)
            {if(_history is not null) await _history.DisposeAsync();_history=new(directory);_historyDirectory=directory;}
            int? Filter(string text)=>string.IsNullOrWhiteSpace(text)?null:int.Parse(text);
            var generation=Filter(GenerationFilter.Text);var policy=Filter(PolicyFilter.Text);var trainer=string.IsNullOrWhiteSpace(TrainerFilter.Text)?null:TrainerFilter.Text.Trim();
            bool? success=OutcomeFilter.SelectedIndex==0?null:OutcomeFilter.SelectedIndex==1;
            bool? evaluation=SourceFilter.SelectedIndex==0?null:SourceFilter.SelectedIndex==2;
            var best=HistoryView.SelectedIndex==1;
            var store=_history!;
            var runs=await Task.Run(()=>store.List(best,generation,policy,trainer,success,evaluation));
            Runs.Clear();foreach(var run in runs) Runs.Add(run);
        }
        catch(Exception error) {Status="History: "+error.Message;}
    }
    private async void SelectRun(object sender,SelectionChangedEventArgs e)
    {
        if(RunList.SelectedItem is not RunSummary summary || _history is null) return;
        try
        {
            var record=await _history.LoadAsync(summary.RunId);
            if((RunList.SelectedItem as RunSummary)?.RunId!=summary.RunId) return;
            if(!System.Text.RegularExpressions.Regex.IsMatch(record.Summary.RulesHash,"^[A-Za-z0-9_-]+$"))throw new InvalidDataException("Invalid history rules identity.");
            var rulesPath=Path.Combine(_historyDirectory!,"rules-"+record.Summary.RulesHash+".json");
            _historyRules=JsonSerializer.Deserialize<RulesManifest>(await File.ReadAllTextAsync(rulesPath))!;
            if(_historyRules.Hash!=record.Summary.RulesHash)throw new InvalidDataException("History rules hash does not match the selected run.");
            _selected=record;_day=0;
            SelectedRunLabel=$"{(summary.Pinned?"Pinned · ":"")}{summary.Trainer} · generation {summary.Generation}, policy {summary.Policy}\n"+
                $"{summary.Wins} wins · {summary.Lives} lives · {summary.Days} days · {(summary.Evaluation?"Evaluation":"Training")}";
            DisplayDay();
        }
        catch(Exception error) {Status="History load: "+error.Message;}
    }
    private void PreviousDay(object sender,RoutedEventArgs e) {if(_selected is null)return;_day=Math.Max(0,_day-1);DisplayDay();}
    private void NextDay(object sender,RoutedEventArgs e) {if(_selected is null)return;_day=Math.Min(_selected.Days.Length-1,_day+1);DisplayDay();}
    private void ChangeBoard(object sender,RoutedEventArgs e) {if(IsInitialized)DisplayDay();}
    private void TogglePin(object sender,RoutedEventArgs e)
    {
        if(_selected is null||_history is null)return;
        try
        {
            var pinned=_history.TogglePin(_selected.Summary.RunId);
            _selected=_selected with {Summary=_selected.Summary with {Pinned=pinned}};
            Status=pinned?"Run pinned; automatic retention will preserve it.":"Run unpinned.";
        }
        catch(Exception error){Status=error.Message;}
    }
    private void DisplayDay()
    {
        if(_selected is null||_selected.Days.Length==0)return;
        var d=_selected.Days[_day];var enemy=ShowOpponent.IsChecked==true;var o=d.BeforeBattle;
        DayLabel=$"Day {d.Day} · {(d.Result.PlayerWon?"Win":"Loss")} · {d.WinsAfter} wins, {d.LivesAfter} lives after battle\n"+
            (enemy?$"Opponent: {d.Opponent.Trainer} · HP {d.Opponent.MaxHp:N0}":$"Before battle: ${o.Gold:N0} · rank {o.Rank} · HP {o.MaxHp:N0} · items {o.ItemUses}/{o.MaximumItemUses}");
        Team.Clear();Bench.Clear();
        var units=enemy?d.Opponent.Team:o.Units;
        for(var i=0;i<6;i++) Team.Add(Card(units[i],i));
        if(!enemy)for(var i=6;i<10;i++) Bench.Add(Card(units[i],i-6));
        var bag=enemy?d.Opponent.Bag:o.Bag;
        Inventory="Trinkets: "+(bag.Length==0?"none":string.Join(" · ",bag.Select(x=>$"{(_historyRules??_rules).Trinkets.FirstOrDefault(t=>t.Id==x.Id)?.Name??x.Id} ×{x.Quantity}")));
        Decisions=d.Choices.Length==0?"No event or reward selections recorded this day.":string.Join("\n",d.Choices);
    }
    private HistoryCard Card(Unit? unit,int slot)
    {
        if(unit is null)return new($"{slot+1}. Empty","","","",null);
        var species=(_historyRules??_rules).Species.FirstOrDefault(x=>x.Id==unit.SpeciesId);
        var level=species is null?null:LevelRules.Resolve(species,unit.Level,unit.Shiny);
        var sprite=Path.Combine(_root,"assets","wiki","monsters",unit.SpeciesId+(unit.Shiny?"-shiny":"")+".png");
        if(!File.Exists(sprite))sprite=Path.Combine(_root,"assets","wiki","monsters",unit.SpeciesId+".png");
        var progress=unit.Counters.Where(x=>x.Key.StartsWith("evolution:") || x.Key.StartsWith("ability:") || x.Key.StartsWith("hatch:"))
            .Select(x=>$"{(x.Key.StartsWith("evolution:")?"Evolution "+x.Key.Split(':')[^1]:x.Key=="ability:team-hp"?"HP growth":"Hatch days")} {x.Value:0.##}");
        return new($"{slot+1}. {species?.Name??unit.SpeciesId}",$"Lv. {unit.Level}{(unit.Shiny?" · Shiny":"")} · base {level?.Cooldown:0.##}s",
            string.Join(" / ",(species?.Types??[]).Concat(unit.AddedTypes).Distinct()),
            string.Join(" · ",Enumerable.Range(0,9).Where(i=>(level?.Stats[i]??0)+unit.Bonuses[i]!=0).Select(i=>i==(int)Stat.Speed?
                $"Speed {(level?.Stats[i]??0)+unit.Bonuses[i]:P0}":$"{(Stat)i} {(level?.Stats[i]??0)+unit.Bonuses[i]:0.##}").Concat(progress)),File.Exists(sprite)?sprite:null);
    }
    public void RenderHistory(string path,string recordPath)
    {
        using var file=File.OpenRead(recordPath);using var zip=new System.IO.Compression.BrotliStream(file,System.IO.Compression.CompressionMode.Decompress);
        _selected=JsonSerializer.Deserialize<RunRecord>(zip)!;
        _historyRules=JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(recordPath)!,"rules-"+_selected.Summary.RulesHash+".json")))!;
        Tabs.SelectedIndex=1;Status="Saved Pantra simulation history · assumptions remain labelled in rule provenance.";
        Runs.Add(_selected.Summary);SelectedRunLabel=$"{_selected.Summary.Trainer} · generation {_selected.Summary.Generation}, policy {_selected.Summary.Policy}";
        DisplayDay();RenderWindow(path);
    }
    private void RenderWindow(string path)
    {
        var content=(FrameworkElement)Content;content.Measure(new Size(1320,920));content.Arrange(new Rect(0,0,1320,920));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1320,920,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    public void RenderPreview(string path)
    {
        Tabs.SelectedIndex=1;_rules=RuleCompiler.Fixture();
        Status="Synthetic diagnostic preview — these are not game performance results.";
        var env=new GameEnvironment(_rules);env.Reset(1,"fixture");env.Step(env.GetLegalActions().First(x=>x.Kind==Move.Buy));
        var before=env.GetObservation();var board=FrozenBoard.From(env.State);var result=new Combat(_rules).ResolveBattle(board,board,1);
        var summary=new RunSummary("preview",2,1,"fixture",10,4,12,true,false,DateTimeOffset.UtcNow,_rules.Hash);
        Runs.Add(summary);SelectedRunLabel="Diagnostic preview · generation 2, policy 1";
        _selected=new(summary,[new(1,before,board,result,1,10,["Example reward choice"])]);DisplayDay();
        RenderWindow(path);
    }
}
