namespace BatomonHelper.Simulation;

public sealed record GuidedPolicyMemory(int Day,int Decisions,ShopPolicyMemory Fallback);

/// <summary>A bounded build-aware teacher. It follows available offers, rather than inventing units or stats.</summary>
public sealed class GuidedShopPolicy(RulesManifest rules,StrategyKnowledgeGuide guide,string[] core)
{
    private readonly StrongestShopPolicy _fallback=new(rules);
    private int _day=-1,_decisions;
    public GuidedPolicyMemory Snapshot()=>new(_day,_decisions,_fallback.Snapshot());
    public void Restore(GuidedPolicyMemory memory){_day=memory.Day;_decisions=memory.Decisions;_fallback.Restore(memory.Fallback);}
    public SimAction Choose(GameEnvironment env){
        var state=env.State;if(state.Day!=_day){_day=state.Day;_decisions=0;}_decisions++;
        var legal=env.GetLegalActions();var observation=env.GetObservation();
        if(state.Phase!=Phase.Shop)return _fallback.Choose(env);
        if(_decisions>=56)return new(Move.Finish);
        var deploy=legal.FirstOrDefault(a=>a.Kind==Move.Move&&a.Source is >=6 and <10&&a.Target<6&&state.Units[a.Target] is null);
        if(deploy is not null)return deploy;
        var owned=state.Units.OfType<Unit>().Select(u=>u.SpeciesId).ToHashSet();
        var candidates=legal.Where(a=>a.Kind==Move.Buy&&state.Shop[a.Source]?.Creature is {} unit&&core.Contains(unit.SpeciesId))
            .OrderByDescending(a=>guide.Prior(observation,a)).ToArray();
        if(candidates.FirstOrDefault() is {} purchase){
            var unit=state.Shop[purchase.Source]!.Creature!;
            var direct=candidates.FirstOrDefault(a=>a.Source==purchase.Source&&a.Target is >=0 and <6);
            if(direct is not null)return direct;
            var copies=state.Units.OfType<Unit>().Count(u=>u.SpeciesId==unit.SpeciesId&&u.Level==unit.Level);
            if(copies>0)return purchase;
            var replace=legal.Where(a=>a.Kind==Move.Sell&&a.Source<6&&!core.Contains(state.Units[a.Source]!.SpeciesId)).MinBy(a=>_fallback.Strength(state.Units[a.Source]!));
            if(replace is not null&&owned.Any(core.Contains))return replace;
        }
        // Only rearrange when an actual rule-derived relationship improves.
        var move=legal.Where(a=>a.Kind==Move.Move&&a.Source<6&&a.Target<6).MaxBy(a=>guide.Prior(observation,a));
        if(move is not null&&guide.Prior(observation,move)>0)return move;
        return _fallback.Choose(env);
    }
    public static LeagueEntry[] Archive(RulesManifest rules,StrategyKnowledgeGuide guide,ulong seed,IReadOnlyList<LeagueEntry> baseline){
        var results=new List<LeagueEntry>();var cores=guide.BuildCores;
        for(var variant=0;variant<Math.Min(6,cores.Length);variant++){
            var env=new GameEnvironment(rules);env.Reset(seed+(ulong)variant*100003,rules.Trainers[variant%rules.Trainers.Length].Id);
            var policy=new GuidedShopPolicy(rules,guide,cores[variant]);var rng=new SeedStream(seed+(ulong)variant);var count=0;
            for(var step=0;step<2000&&env.State.Phase!=Phase.Complete;step++){
                var action=env.State.Phase==Phase.Shop&&count>=60?new SimAction(Move.Finish):policy.Choose(env);
                if(action.Kind==Move.Finish)results.Add(new(-2,variant,"scripted",FrozenBoard.From(env.State)));
                var result=env.Step(action,b=>{var pool=baseline.Where(e=>e.Board.Day==b.Day).ToArray();if(pool.Length==0)throw new InvalidOperationException("Missing scripted opponent day.");return pool[rng.Index(pool.Length)].Board;},rng.Next());
                count=result.Battle is null?count+1:0;
            }
        }
        return results.ToArray();
    }
}
