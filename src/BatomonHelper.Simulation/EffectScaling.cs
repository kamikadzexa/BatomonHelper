namespace BatomonHelper.Simulation;

internal static class EffectScaling
{
    public static double Factor(Effect effect, Unit? source, IEnumerable<Unit> team, IEnumerable<BagEntry> bag,
        IReadOnlyDictionary<string, Species> species, Func<Unit, double[]> stats, double[]? enemyStatus = null, double lostLives = 0,
        IReadOnlyDictionary<string, InventoryDefinition>? trinkets = null, double[]? eventStats = null, int wins = 0, double itemsUsed = 0) => effect.Scale switch
    {
        EffectScale.None => 1,
        EffectScale.Trinkets => bag.Sum(x => x.Quantity),
        EffectScale.DistinctTrinkets => bag.Where(x => x.Quantity > 0).Select(x => x.Id).Distinct().Count(),
        EffectScale.CommonTrinkets => bag.Where(x => trinkets![x.Id].Rarity == 0).Sum(x => x.Quantity),
        EffectScale.UniqueTeamTypes => team.SelectMany(x => UnitTypes.Resolve(x, species)).Distinct().Count(),
        EffectScale.Allies => team.Count(x => (source is null || !ReferenceEquals(x, source)) &&
            (effect.ScaleType is null || UnitTypes.Resolve(x, species).Contains(effect.ScaleType)) &&
            (effect.ScaleRarity is null || species[x.SpeciesId].Rarity == effect.ScaleRarity) &&
            (effect.ScaleMinimumLevel is null || x.Level >= effect.ScaleMinimumLevel)),
        EffectScale.EnemyStatus => enemyStatus![(int)effect.ScaleStat - (int)Stat.Burn],
        EffectScale.SelfStat => source is null ? throw new InvalidOperationException("A scaled self-stat effect requires a source.") : stats(source)[(int)effect.ScaleStat],
        EffectScale.LostLives => lostLives,
        EffectScale.EventStat => eventStats![(int)effect.ScaleStat],
        EffectScale.AlliedStat => team.Sum(x => stats(x)[(int)effect.ScaleStat]),
        EffectScale.Badges => wins,
        EffectScale.ItemsUsed => itemsUsed,
        _ => throw new NotSupportedException(effect.Scale.ToString())
    };
}
