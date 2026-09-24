namespace BatomonHelper.Core;

public static class CreatureCatalogFilter
{
    public static IEnumerable<MonsterDefinition> Apply(CatalogSnapshot catalog, string? searchText, MonsterRegionFilter region, Rarity? rarity)
    {
        var search = searchText?.Trim();
        return catalog.Monsters
            .Where(monster => monster.BaseSpeciesId is null)
            .Where(monster => string.IsNullOrWhiteSpace(search) || monster.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(monster => region == MonsterRegionFilter.All || monster.Regions.Contains($"{region} region"))
            .Where(monster => rarity is null || monster.Rarity == rarity)
            .OrderBy(monster => monster.Rarity).ThenBy(monster => monster.Name);
    }
}
