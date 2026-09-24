using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class CreatureCatalogFilterTests
{
    [Fact]
    public void Applies_name_region_and_rarity_filters_together()
    {
        var catalog = new CatalogSnapshot("test", "1", null, DateTimeOffset.UtcNow, [], "hash", [],
        [
            Monster("Beetbud", Rarity.Common, "Jinto region"),
            Monster("Beetdown", Rarity.Rare, "Jinto region"),
            Monster("Bunchop", Rarity.Common, "Pantra region")
        ]);

        var filtered = CreatureCatalogFilter.Apply(catalog, "beet", MonsterRegionFilter.Jinto, Rarity.Common).ToArray();

        Assert.Equal("Beetbud", Assert.Single(filtered).Name);
    }

    [Fact]
    public async Task Loads_the_committed_offline_catalog_with_sprite_paths()
    {
        var database = Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db");
        var catalog = await new SnapshotRepository(database).GetLatestAsync();

        Assert.NotNull(catalog);
        Assert.Equal(149, catalog!.Monsters.Length);
        Assert.All(catalog.Monsters, monster => Assert.Equal($"assets/wiki/monsters/{monster.Id}.png", monster.SpritePath));
        Assert.Equal(0, catalog.Items.Single(item => item.Name == "Green Stone").Price);
    }

    private static MonsterDefinition Monster(string name, Rarity rarity, string region) => new(name.ToLowerInvariant(), name, rarity, null, ImmutableHashSet.Create("starter"))
    {
        Regions = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, region)
    };
}
