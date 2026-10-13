namespace BatomonHelper.Simulation;

public sealed record LeagueEntry(int Generation,int Policy,string Kind,FrozenBoard Board,string? SessionId=null);

/// <summary>Single-writer league; immutable during parallel simulation steps.</summary>
public sealed class OpponentLeague
{
    private readonly LinkedList<LeagueEntry> _entries=[];
    private readonly Dictionary<LeagueEntry,LinkedListNode<LeagueEntry>> _nodes=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(int,int,string,int,string,int,string?),List<LeagueEntry>> _detail=[];
    private readonly Dictionary<(int,int,string,int,string?),List<LeagueEntry>> _groups=[];
    private readonly Dictionary<(int,string),List<LeagueEntry>> _buckets=[];
    public int Generation {get;private set;}
    public int Count=>_entries.Count;
    public LeagueEntry[] Entries=>_entries.ToArray();
    private (int,int,string,int,string,int,string?) Detail(LeagueEntry e)=>
        (e.Generation,e.Policy,e.Kind,e.Board.Day,e.Board.Trainer,Math.Min(2,e.Board.Wins*3/Math.Max(1,e.Board.Day)),e.SessionId);
    private (int,int,string,int,string?) Group(LeagueEntry e)=>(e.Generation,e.Policy,e.Kind,e.Board.Day,e.SessionId);
    private IEnumerable<string> Categories(LeagueEntry e)
    {
        yield return "all";
        if(e.Kind is "baseline" or "user" or "champion")yield return e.Kind;
        if(e.Kind=="current" && e.Generation==Generation)yield return "current";
        if(e.Kind=="current" && e.Generation<Generation && e.Generation>=Generation-19)yield return "recent";
    }
    private static List<LeagueEntry> List<TKey>(Dictionary<TKey,List<LeagueEntry>> map,TKey key) where TKey:notnull
    {if(!map.TryGetValue(key,out var result))map[key]=result=[];return result;}
    public void Add(LeagueEntry entry)
    {
        _nodes[entry]=_entries.AddLast(entry);
        var detail=List(_detail,Detail(entry));detail.Add(entry);
        var group=List(_groups,Group(entry));group.Add(entry);
        foreach(var category in Categories(entry))List(_buckets,(entry.Board.Day,category)).Add(entry);
        // User recordings preserve a wider range of boards; their per-session cap is still 32/day.
        if(detail.Count>(entry.Kind=="user"?32:2))Remove(detail[0]);
        if(group.Count>32)Remove(group[0]);
    }
    private void Remove(LeagueEntry entry)
    {
        if(!_nodes.Remove(entry,out var node))return;
        _entries.Remove(node);_detail[Detail(entry)].Remove(entry);_groups[Group(entry)].Remove(entry);
        foreach(var category in Categories(entry))_buckets[(entry.Board.Day,category)].Remove(entry);
    }
    public void Restore(IEnumerable<LeagueEntry> entries,int generation)
    {
        _entries.Clear();_nodes.Clear();_detail.Clear();_groups.Clear();_buckets.Clear();Generation=generation;
        foreach(var entry in entries)Add(entry);
        SetGeneration(generation);
    }
    public void SetGeneration(int generation)
    {
        // Remove against the old index membership, then change generation and rebuild category indexes once.
        var champions=_entries.Where(x=>x.Kind=="champion").Select(x=>(x.Generation,x.Policy)).Distinct()
            .OrderByDescending(x=>x.Generation).Take(20).ToHashSet();
        foreach(var entry in _entries.Where(x=>x.Kind=="current"&&x.Generation<generation-19 ||
            x.Kind=="champion"&&!champions.Contains((x.Generation,x.Policy))).ToArray())Remove(entry);
        Generation=generation;_buckets.Clear();
        foreach(var entry in _entries)foreach(var category in Categories(entry))List(_buckets,(entry.Board.Day,category)).Add(entry);
    }
    public IReadOnlyList<LeagueEntry> Candidates(int day,string category)=>
        _buckets.TryGetValue((day,category),out var values)?values:Array.Empty<LeagueEntry>();
    public LeagueEntry? Pick(int day,ulong seed,bool evaluation=false)
    {
        var rng=new SeedStream(seed^0xAE61UL);var roll=rng.Uniform();
        var user=Candidates(day,"user");
        var category=roll<.4?"current":roll<.65?"recent":roll<.85?"champion":roll<.95||user.Count==0?"baseline":"user";
        var pool=Candidates(day,category);if(pool.Count==0)pool=Candidates(day,"baseline");if(pool.Count==0)pool=Candidates(day,"all");
        if(pool.Count==0 && evaluation)throw new InvalidOperationException("Independent same-day opponent archive missing for day "+day);
        return pool.Count==0?null:pool[rng.Index(pool.Count)];
    }
}
