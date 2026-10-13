using System.Security.Cryptography;
using System.Text.Json;

namespace BatomonHelper.Simulation;

public sealed record UserDecision(SimAction Action,ulong BattleSeed,FrozenBoard? Opponent,RunState After);
public sealed record UserSession(int Version,string Id,string RulesHash,ulong Seed,string Trainer,RunState Initial,
    RunState Current,UserDecision[] Decisions,DayRecord[] Days,bool InOpponentPool=false,bool Enabled=true);

public sealed class UserSessionStore(string directory,RulesManifest rules)
{
    public string DirectoryPath=>Path.GetFullPath(directory);
    public UserSession[] List()
    {
        if(!Directory.Exists(DirectoryPath))return [];
        return Directory.EnumerateFiles(DirectoryPath,"*.json").Order().Select(Read).ToArray();
    }
    private UserSession Read(string path)=>JsonSerializer.Deserialize<UserSession>(File.ReadAllText(path))??throw new InvalidDataException("Empty user session.");
    private string PathFor(string id)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,80}$"))throw new InvalidDataException("Invalid user session ID.");
        return Path.Combine(DirectoryPath,id+".json");
    }
    public void Save(UserSession session)
    {
        if(session.RulesHash!=rules.Hash||session.Version!=2)throw new InvalidDataException("Incompatible user session.");
        Directory.CreateDirectory(DirectoryPath);var path=PathFor(session.Id);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(session));File.Move(path+".tmp",path,true);
    }
    public void Delete(string id)=>File.Delete(PathFor(id));
    public void Validate(UserSession session)
    {
        if(session.Version!=2||session.RulesHash!=rules.Hash)throw new InvalidDataException("User session rules differ.");
        var env=new GameEnvironment(rules);var initial=env.Reset(session.Seed,session.Trainer);
        if(JsonSerializer.Serialize(env.State)!=JsonSerializer.Serialize(session.Initial))throw new InvalidDataException("User session initial state differs.");
        var days=new List<DayRecord>();
        foreach(var decision in session.Decisions){
            var result=env.Step(decision.Action,decision.Opponent is null?null:_=>decision.Opponent,decision.BattleSeed);
            if(JsonSerializer.Serialize(env.State)!=JsonSerializer.Serialize(decision.After))throw new InvalidDataException("User session replay differs.");
            if(result.DayRecord is {} day)days.Add(day);
        }
        if(JsonSerializer.Serialize(env.State)!=JsonSerializer.Serialize(session.Current)||JsonSerializer.Serialize(days)!=JsonSerializer.Serialize(session.Days))
            throw new InvalidDataException("User session final state or battle history differs.");
    }
    public LeagueEntry[] Opponents()
    {
        var result=new List<LeagueEntry>();var seen=new HashSet<string>();
        foreach(var session in List().Where(x=>x.Version==2&&x.InOpponentPool&&x.Enabled&&x.RulesHash==rules.Hash)){
            Validate(session);
            foreach(var group in session.Days.GroupBy(x=>x.Day))foreach(var day in group.Take(32)){
                var o=day.BeforeBattle;var board=new FrozenBoard(day.Day,o.MaxHp,o.Trainer,o.Units.Take(6).Select(x=>x?.Clone()).ToArray(),[..o.Bag],[..o.ExtraTrainers],
                    o.Counters is null?null:new(o.Counters),o.Rank,o.VisibleState?.ForeignPool,o.Wins);
                var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(board)));
                if(seen.Add(hash))result.Add(new(-2,0,"user",board,session.Id));
            }
        }return result.ToArray();
    }
}

public sealed class PlaySession
{
    private readonly RulesManifest _rules;
    private readonly LeagueEntry[] _opponents;
    public GameEnvironment Environment {get;}
    public UserSession Session {get;private set;}
    public PlaySession(RulesManifest rules,ulong seed,string trainer)
    {
        _rules=rules;_opponents=IndependentOpponentArchive.Build(rules,seed^0xBA5EUL);Environment=new(rules);Environment.Reset(seed,trainer);
        var initial=Environment.State.Clone();Session=new(2,Guid.NewGuid().ToString("N"),rules.Hash,seed,trainer,initial,initial.Clone(),[],[]);
    }
    public PlaySession(RulesManifest rules,UserSession session,UserSessionStore store)
    {store.Validate(session);_rules=rules;_opponents=IndependentOpponentArchive.Build(rules,session.Seed^0xBA5EUL);Environment=new(rules);Environment.Restore(session.Current);Session=session;}
    public StepResult Apply(SimAction action)
    {
        FrozenBoard? opponent=null;var seed=TrainingObjective.EvaluationOpponentSeed(Session.Seed,0,Session.Decisions.Length,Environment.State.Day);
        if(action.Kind==Move.Finish){
            var candidates=_opponents.Where(x=>x.Board.Day==Environment.State.Day).ToArray();
            if(candidates.Length==0)throw new InvalidOperationException("Independent same-day opponent archive is missing.");
            opponent=candidates[(int)(seed%(ulong)candidates.Length)].Board;
        }
        var result=Environment.Step(action,opponent is null?null:_=>opponent,seed);
        var decision=new UserDecision(action,seed,opponent is null?null:StateSnapshots.Copy(opponent),Environment.State.Clone());
        Session=Session with {Current=Environment.State.Clone(),Decisions=Session.Decisions.Append(decision).ToArray(),Days=result.DayRecord is null?Session.Days:Session.Days.Append(StateSnapshots.Copy(result.DayRecord)).ToArray()};
        return result;
    }
    public void SetPool(bool included,bool enabled=true)=>Session=Session with {InOpponentPool=included,Enabled=enabled};
}
