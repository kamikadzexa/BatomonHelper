using BatomonHelper.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatomonHelper.Simulation;

public sealed record StrategyOpportunity(string Name,string[] Owned,string[] Missing,string Source);
/// <summary>Evidence affects proposals, never the game's reward. Ability relationships work without wiki data.</summary>
public sealed class StrategyKnowledgeGuide
{
    private readonly RulesManifest _rules;
    private readonly Dictionary<string,Species> _species;
    private readonly (string Name,string[] Core,string Source)[] _builds;
    public string Identity {get;}
    public string[] Warnings {get;}
    public string[][] BuildCores=>_builds.Where(b=>b.Core.Length>=3).Select(b=>b.Core.ToArray()).ToArray();
    public StrategyKnowledgeGuide(RulesManifest rules,StrategyKnowledge? knowledge=null,bool enabled=true){
        _rules=rules;_species=rules.Species.ToDictionary(x=>x.Id);
        Identity=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {Knowledge=knowledge??StrategyKnowledge.Empty,Enabled=enabled})));
        var warnings=new List<string>();
        var compatible=enabled && knowledge is not null && knowledge.Balance==rules.Balance;
        if(!enabled)warnings.Add("Wiki priors disabled for this experiment; mechanics-derived relationships remain enabled.");
        if(enabled&&knowledge is not null&&!compatible)warnings.Add($"Wiki Balance {knowledge.Balance} does not match simulator Balance {rules.Balance}; wiki priors disabled.");
        var builds=new List<(string,string[],string)>();
        if(compatible){
            foreach(var pair in knowledge!.Pairs){var ids=new[]{pair.FirstId,pair.SecondId};
                if(ids.All(_species.ContainsKey))builds.Add((string.Join(" + ",ids),ids,pair.SourceUrl));else warnings.Add("Unknown wiki pair: "+pair.Id);}
            foreach(var composition in knowledge.Compositions){
                if(composition.CoreIds.All(_species.ContainsKey))builds.Add((composition.Name,composition.CoreIds.ToArray(),composition.SourceUrl));else warnings.Add("Unknown wiki composition: "+composition.Id);}
        }
        _builds=builds.ToArray();Warnings=warnings.ToArray();
    }
    public StrategyOpportunity[] Opportunities(Observation observation){
        var owned=observation.Units.OfType<Unit>().Select(x=>x.SpeciesId).ToHashSet();
        return _builds.Where(b=>b.Core.Any(owned.Contains)).Select(b=>new StrategyOpportunity(b.Name,b.Core.Where(owned.Contains).ToArray(),b.Core.Where(x=>!owned.Contains(x)).ToArray(),b.Source))
            .OrderBy(x=>x.Missing.Length).ThenByDescending(x=>x.Owned.Length).Take(12).ToArray();
    }
    public double Compatibility(Unit left,Unit right,bool adjacent){
        var a=_species[left.SpeciesId];var b=_species[right.SpeciesId];double score=0;
        var effects=LevelRules.Resolve(a,left.Level,left.Shiny).Effects;
        var other=LevelRules.Resolve(b,right.Level,right.Shiny);
        foreach(var e in effects){
            var eventMatches=(e.EventType is null||b.Types.Concat(right.AddedTypes).Contains(e.EventType))&&e.EventExcludeSpecies!=b.Id&&
                (e.EventMinimumLevel is null||right.Level>=e.EventMinimumLevel)&&(!e.EventAdjacent||adjacent);
            if(eventMatches){
                var eventStat=e.Trigger switch {Trigger.AllyBurn or Trigger.TeamBurn=>Stat.Burn,Trigger.AllyPoison=>Stat.Poison,Trigger.AllyShock=>Stat.Shock,Trigger.AllyShield or Trigger.TeamShield=>Stat.Shield,Trigger.TeamDamage=>Stat.Damage,_=>(Stat?)null};
                if(e.Trigger==Trigger.AllyCast&&other.Cooldown.HasValue || eventStat is {} generated&&other.Stats[(int)generated]+right.Bonuses[(int)generated]>0)score+=.3;
            }
            if(e.Scale==EffectScale.EnemyStatus&&e.ScaleStat is Stat.Burn or Stat.Poison or Stat.Shock&&other.Stats[(int)e.ScaleStat]+right.Bonuses[(int)e.ScaleStat]>0)score+=.3;
            if(e.Scale==EffectScale.AlliedStat&&other.Stats[(int)e.ScaleStat]+right.Bonuses[(int)e.ScaleStat]>0)score+=.2;
            if(e.ExcludeSpecies==b.Id || e.Type is {} type&&!type.Split('|').Any(t=>b.Types.Concat(right.AddedTypes).Contains(t)))continue;
            if(e.Target==Target.Adjacent&&!adjacent)continue;
            if(e.Target is Target.Enemies or Target.RandomEnemy or Target.Opposite or Target.EnemyRow or Target.Self)continue;
            if(e.Operation is Operation.Charge or Operation.Trigger or Operation.RepeatOngoing)score+=.4;
            if(e.Operation is Operation.Add or Operation.Multiply && e.Trigger==Trigger.Ongoing)score+=.15;
            if(e.EventType is {} eventType&&b.Types.Contains(eventType))score+=.2;
        }
        if(_builds.Any(build=>build.Core.Contains(a.Id)&&build.Core.Contains(b.Id)))score+=.35;
        return score;
    }
    public double Prior(Observation o,SimAction action){
        double strength(Unit unit){var l=LevelRules.Resolve(_species[unit.SpeciesId],unit.Level,unit.Shiny);return Math.Log(1+l.Stats.Zip(unit.Bonuses,(a,b)=>Math.Max(0,a+b)).Take(6).Sum())/4;}
        switch(action.Kind){
            case Move.Buy:
                var unit=o.Shop[action.Source]!.Creature!;var bonus=o.Units.OfType<Unit>().Sum(u=>Compatibility(unit,u,true)+Compatibility(u,unit,true));
                return 1+strength(unit)+bonus+(action.Target is >=0 and <6?1:0)+(o.Units.OfType<Unit>().Any(u=>_species[u.SpeciesId].MergeGroup==_species[unit.SpeciesId].MergeGroup&&u.Level==unit.Level)?1:0);
            case Move.Move:
                if(action.Source is >=6 and <10 && action.Target<6&&o.Units[action.Target] is null)return 3;
                if(action.Source<6&&action.Target<6){
                    double layout(Unit?[] units)=>Enumerable.Range(0,6).Where(i=>units[i] is not null).Sum(i=>Enumerable.Range(0,6).Where(j=>j!=i&&units[j] is not null).Sum(j=>Compatibility(units[i]!,units[j]!,Combat.Targets(i,Target.Adjacent).Contains(j))));
                    var units=(Unit?[])o.Units.Clone();(units[action.Source],units[action.Target])=(units[action.Target],units[action.Source]);return -.5+layout(units)-layout(o.Units);
                }return -2;
            case Move.BuyItem:return 1;
            case Move.Sell:return -1-strength(action.Source==10?o.Overflow!:o.Units[action.Source]!);
            case Move.Reroll:return o.Gold>6||o.FreeRerolls>0?.2:-1;
            case Move.Lock:return -.5;
            case Move.Finish:return o.Units.Take(6).Any(x=>x is null)&&o.Units.Skip(6).Any(x=>x is not null)?-4:.5;
            case Move.Choose:return 1+o.Choices[action.Choice].Effects.Sum(e=>e.Operation is Operation.GrantUnit or Operation.GrantTrinket?.5:0);
            default:return 0;
        }
    }
}
