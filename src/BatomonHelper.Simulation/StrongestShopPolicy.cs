namespace BatomonHelper.Simulation;

public sealed record ShopPolicyMemory(int Day,int Rerolls,int Decisions);

/// <summary>Independent, bounded shopping baseline. Used for opponents and comparisons, never imitation.</summary>
public sealed class StrongestShopPolicy(RulesManifest rules)
{
    private readonly Dictionary<string,Species> _species=rules.Species.ToDictionary(x=>x.Id);
    private int _day=-1,_rerolls,_decisions;
    public ShopPolicyMemory Snapshot()=>new(_day,_rerolls,_decisions);
    public void Restore(ShopPolicyMemory memory){_day=memory.Day;_rerolls=memory.Rerolls;_decisions=memory.Decisions;}
    public double Strength(Unit u)
    {
        var l=LevelRules.Resolve(_species[u.SpeciesId],u.Level,u.Shiny);var s=l.Stats.Zip(u.Bonuses,(a,b)=>a+b).ToArray();
        var output=s[0]+.6*s[1]+.6*s[2]+2*s[3]+2*s[4]+2*s[5];
        var passive=l.Effects.Where(x=>x.Trigger is Trigger.Ongoing or Trigger.BattleStart).Sum(x=>Math.Abs(x.Amount)*(x.Percent?20:1));
        return output*(1+Math.Max(0,s[7]))/Math.Max(.1,(l.Cooldown??10)/(1+Math.Max(0,s[6])))+passive;
    }
    public SimAction Choose(GameEnvironment env)
    {
        var s=env.State;var legal=env.GetLegalActions();
        if(s.Day!=_day){_day=s.Day;_rerolls=0;_decisions=0;}
        _decisions++;
        if(s.Phase is Phase.Reward or Phase.Event or Phase.TrainerChoice)return legal.Where(x=>x.Kind==Move.Choose).MaxBy(x=>s.Choices[x.Choice].Effects.Sum(e=>Math.Abs(e.Amount)))!;
        if(s.Phase==Phase.Overflow){var weakest=legal.Where(x=>x.Kind==Move.Sell).MinBy(x=>Strength(x.Source==10?s.Overflow!:s.Units[x.Source]!))!;
            return legal.FirstOrDefault(x=>x.Kind==Move.Move&&x.Source==10&&x.Target<6)??weakest;}
        if(_decisions>=60)return new(Move.Finish);
        if(legal.FirstOrDefault(x=>x.Kind==Move.Move&&x.Source>=6&&x.Source<10&&x.Target<6&&s.Units[x.Target] is null) is {} deploy)return deploy;
        var offers=legal.Where(x=>x.Kind==Move.Buy).GroupBy(x=>x.Source).Select(x=>x.First()).ToArray();
        var weakestSlot=Enumerable.Range(0,6).Where(i=>s.Units[i] is not null).OrderBy(i=>Strength(s.Units[i]!)).FirstOrDefault(-1);
        var buy=offers.OrderByDescending(x=>Strength(s.Shop[x.Source]!.Creature!)).FirstOrDefault();
        if(buy is not null){var unit=s.Shop[buy.Source]!.Creature!;var matches=s.Units.Any(x=>x is not null&&_species[x.SpeciesId].MergeGroup==_species[unit.SpeciesId].MergeGroup&&x.Level==unit.Level);
            if(matches||s.Units.Take(6).Any(x=>x is null)||weakestSlot>=0&&Strength(unit)>Strength(s.Units[weakestSlot]!)*1.1){
                var direct=legal.FirstOrDefault(x=>x.Kind==Move.Buy&&x.Source==buy.Source&&x.Target<6&&x.Target>=0);
                if(direct is not null)return direct;
                if(matches)return buy;
                return legal.First(x=>x.Kind==Move.Sell&&x.Source==weakestSlot);
            }}
        if(legal.FirstOrDefault(x=>x.Kind==Move.BuyItem) is {} item)return item;
        var budget=s.Rank<=4?4:s.Rank<=8?8:16;
        if(_rerolls<budget&&(s.FreeRerolls>0||s.Gold>=10)&&legal.FirstOrDefault(x=>x.Kind==Move.Reroll) is {} reroll){_rerolls++;return reroll;}
        return new(Move.Finish);
    }
}
