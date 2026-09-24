using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class SnapshotRepositoryTests
{
    [Fact]
    public async Task Bundled_catalog_contains_the_full_rarity_distribution()
    {
        var database = Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db");
        var catalog = await new SnapshotRepository(database, readOnly: true).GetLatestAsync();

        Assert.NotNull(catalog);
        Assert.Equal(149, catalog!.Monsters.Length);
        Assert.All(Enum.GetValues<Rarity>(), rarity => Assert.Contains(catalog.Monsters, monster => monster.Rarity == rarity));
        Assert.Contains(catalog.Monsters, monster => monster.Rarity != Rarity.Common);
    }

    [Fact]
    public async Task Keeps_the_latest_valid_catalog_snapshot_offline()
    {
        var database = Path.Combine(Path.GetTempPath(), $"batomon-helper-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SnapshotRepository(database);
            await repository.InitializeAsync();
            var snapshot = new CatalogSnapshot("snapshot", "25377295", "22", DateTimeOffset.UtcNow, ["https://batomon.com/"], "hash", [],
                [new MonsterDefinition("alpha", "Alpha", Rarity.Common, null, ImmutableHashSet.Create("starter"))]);
            await repository.SaveAsync(snapshot);

            var loaded = await repository.GetLatestAsync();

            Assert.NotNull(loaded);
            Assert.Equal(snapshot.SnapshotId, loaded!.SnapshotId);
            Assert.Equal("alpha", Assert.Single(loaded.Monsters).Id);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database) + "*")) File.Delete(file);
        }
    }
}
