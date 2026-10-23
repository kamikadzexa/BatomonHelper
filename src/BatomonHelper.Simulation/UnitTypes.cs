namespace BatomonHelper.Simulation;

internal static class UnitTypes
{
    public static IEnumerable<string> Resolve(Unit unit, IReadOnlyDictionary<string, Species> species)
    {
        var types = species[unit.SpeciesId].Types.Concat(unit.AddedTypes).ToArray();
        // The game uses "all" for a creature with every type and "null" for no type.
        return (types.Contains("all") ? species.Values.SelectMany(value => value.Types) : types)
            .Where(type => type is not ("all" or "null")).Distinct();
    }
}
