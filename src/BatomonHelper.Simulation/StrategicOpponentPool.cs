namespace BatomonHelper.Simulation;

public sealed record MatchupTotals(int Battles,int AgentWins);

/// <summary>Training emphasizes weaknesses while frozen evaluation retains fixed sampling weights.</summary>
public sealed class StrategicOpponentPool(bool adaptive)
{
    public Dictionary<string,MatchupTotals> Matchups {get;private set;}=[];
    public static string Key(LeagueEntry entry)=>$"{entry.Board.Day}/{entry.Kind}/{entry.Generation}/{entry.Policy}/{entry.SessionId}";
    public void Restore(Dictionary<string,MatchupTotals> matchups)=>Matchups=new(matchups);
    public void Record(LeagueEntry entry,bool agentWon){var key=Key(entry);var previous=Matchups.GetValueOrDefault(key)??new(0,0);Matchups[key]=new(previous.Battles+1,previous.AgentWins+(agentWon?1:0));}
    public LeagueEntry Pick(IReadOnlyList<LeagueEntry> entries,int day,ulong seed){
        var rng=new SeedStream(seed);var sameDay=entries.Where(e=>e.Board.Day==day).ToArray();
        var users=sameDay.Where(e=>e.Kind=="user").GroupBy(e=>e.SessionId).ToArray();
        // Selecting a session first makes its cap independent of how many boards it contributes.
        if(users.Length>0&&rng.Uniform()<Math.Min(.3,.1*users.Length)){var session=users[rng.Index(users.Length)].ToArray();return session[rng.Index(session.Length)];}
        var categories=sameDay.Where(e=>e.Kind!="user").GroupBy(e=>e.Kind).ToArray();
        if(categories.Length==0)throw new InvalidOperationException("No independent opponent archive for day "+day);
        double Mass(string kind)=>kind switch {"current"=>.45,"champion"=>.25,"scripted"=>.2,_=>.1};
        var roll=rng.Uniform()*categories.Sum(c=>Mass(c.Key));var category=categories[^1];
        foreach(var candidate in categories){roll-=Mass(candidate.Key);if(roll<0){category=candidate;break;}}
        var groups=category.GroupBy(Key).ToArray();
        double Weight(string key){if(!adaptive||!Matchups.TryGetValue(key,out var totals))return .75;
            return .25+(totals.Battles-totals.AgentWins+1d)/(totals.Battles+2);}
        roll=rng.Uniform()*groups.Sum(g=>Weight(g.Key));var selected=groups[^1];
        foreach(var candidate in groups){roll-=Weight(candidate.Key);if(roll<0){selected=candidate;break;}}
        var boards=selected.ToArray();return boards[rng.Index(boards.Length)];
    }
}
