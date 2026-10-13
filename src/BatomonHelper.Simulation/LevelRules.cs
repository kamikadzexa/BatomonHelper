namespace BatomonHelper.Simulation;

public static class LevelRules
{
    // Four levels are imported. Higher levels remain an explicit extrapolation assumption.
    public static LevelDefinition Resolve(Species species,int level,bool shiny)
    {
        if(level<1)throw new InvalidDataException("Creature level must be positive.");
        var levels=shiny && species.Shiny.Length>0?species.Shiny:species.Normal;
        if(levels.FirstOrDefault(x=>x.Level==level) is { } exact)return exact;
        var last=levels.MaxBy(x=>x.Level)??throw new InvalidDataException("Missing creature level definitions.");
        if(level<last.Level)throw new InvalidDataException("Missing intermediate creature level.");
        var prior=levels.Where(x=>x.Level<last.Level).MaxBy(x=>x.Level)??last;
        var distance=Math.Max(1,last.Level-prior.Level);
        var stats=last.Stats.Select((x,i)=>x+(x-prior.Stats[i])*(level-last.Level)/distance).ToArray();
        return last with {Level=level,Stats=stats};
    }
    public static bool Unlimited(Species species) => species.Normal.Concat(species.Shiny).Any(x=>x.Effects.Any(e=>e.UnlimitedLevels));
}
