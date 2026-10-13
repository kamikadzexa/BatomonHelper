using System.Text.RegularExpressions;
using System.Globalization;

namespace BatomonHelper.Simulation;

public static class EvolutionRules
{
    public static string Counter(Unit unit,Effect effect) => "evolution:"+unit.SpeciesId+":"+effect.ProgressCounter;
    public static bool TryCompile(string text,Trigger trigger,out Effect[] effects)
    {
        effects=[];
        if(text=="Evolve."){effects=[new(trigger,Operation.Evolve,Target.Self,Permanent:true)];return true;}
        var damage=Regex.Match(text,"^Evolve after your team deals ([0-9]+) non-status Damage\\.$");
        if(damage.Success)
        {effects=[new(Trigger.TeamDamage,Operation.ProgressEvolution,Target.Self,Amount:double.Parse(damage.Groups[1].Value,CultureInfo.InvariantCulture),
            Permanent:true,ProgressCounter:"damage",ProgressStat:Stat.Damage)];return true;}
        var burn=Regex.Match(text,"^Evolve when your team inflicts Burn ([0-9]+) times\\.$");
        if(burn.Success)
        {effects=[new(Trigger.TeamBurn,Operation.ProgressEvolution,Target.Self,Amount:double.Parse(burn.Groups[1].Value,CultureInfo.InvariantCulture),Permanent:true,ProgressCounter:"burn")];return true;}
        var trinkets=Regex.Match(text,"^Evolve after collecting ([0-9]+) more Trinkets\\.$");
        if(trinkets.Success)
        {effects=[new(Trigger.TrinketGained,Operation.ProgressEvolution,Target.Self,Amount:double.Parse(trinkets.Groups[1].Value,CultureInfo.InvariantCulture),Permanent:true,ProgressCounter:"trinkets")];return true;}
        var knockout=Regex.Match(text,"^Evolve after this monster experiences Knockout ([0-9]+) times\\.$");
        if(knockout.Success)
        {
            var amount=double.Parse(knockout.Groups[1].Value,CultureInfo.InvariantCulture);
            effects=[new(Trigger.Knockout,Operation.ProgressEvolution,Target.Self,Amount:amount,Permanent:true,ProgressCounter:"knockouts",DeferredEvolution:true),
                new(Trigger.DayStart,Operation.Evolve,Target.Self,Amount:amount,Permanent:true,ProgressCounter:"knockouts",RequireNoOwnedEvolution:true)];return true;
        }
        var hatch=Regex.Match(text,"^Hatches a Legendary Dragon monster in ([0-9]+) days\\.$");
        if(hatch.Success)
        {effects=[new(Trigger.DayStart,Operation.Hatch,Target.Self,Amount:double.Parse(hatch.Groups[1].Value,CultureInfo.InvariantCulture),Permanent:true,Type:"dragon",Rarity:4,MinimumLevel:1)];return true;}
        return false;
    }
}
