using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class SnapshotRepositoryTests
{
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
