namespace BatomonHelper.Simulation;

/// <summary>Only shop Move and Lock preserve the context. Every other action starts a new context.</summary>
public sealed class ShopTurnMemory
{
    private readonly HashSet<string> _visited=[];
    public int Blocked {get;private set;}
    public Move? LastAction {get;private set;}
    public int RepeatedKind {get;private set;}
    public string[] Visited=>_visited.Order().ToArray();
    public void Restore(IEnumerable<string> visited,Move? last=null,int repeated=0)
    {_visited.Clear();_visited.UnionWith(visited);LastAction=last;RepeatedKind=repeated;Blocked=0;}
    public void Reset(){_visited.Clear();LastAction=null;RepeatedKind=0;Blocked=0;}
    private static string Key(RunState state,SimAction? action=null)
    {
        // Unit payload, RNG, choices, counters, economy and pending state cannot change within
        // this context: shop Move only swaps units, and Lock only changes these two booleans.
        var ids=state.Units.Select(x=>x?.Instance??0).ToArray();
        var locked=state.Locked;var carry=state.CarryShop;
        if(action?.Kind==Move.Move)(ids[action.Source],ids[action.Target])=(ids[action.Target],ids[action.Source]);
        if(action?.Kind==Move.Lock){locked=!locked;carry=locked;}
        return string.Join(',',ids)+":"+locked+":"+carry;
    }
    public SimAction[] Filter(RunState state,SimAction[] legal)
    {
        if(state.Phase!=Phase.Shop)return legal;
        _visited.Add(Key(state));
        var result=legal.Where(x=>x.Kind is not (Move.Move or Move.Lock) || !_visited.Contains(Key(state,x))).ToArray();
        Blocked+=legal.Length-result.Length;return result;
    }
    public void Applied(SimAction action,RunState after,bool battle)
    {
        RepeatedKind=LastAction==action.Kind?RepeatedKind+1:1;LastAction=action.Kind;
        if(battle){Reset();return;}
        if(action.Kind is not (Move.Move or Move.Lock) || after.Phase!=Phase.Shop)_visited.Clear();
        if(after.Phase==Phase.Shop)_visited.Add(Key(after));
    }
}
