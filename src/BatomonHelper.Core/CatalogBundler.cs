namespace BatomonHelper.Core;

/// <summary>Build-time utility for the committed offline wiki snapshot. It is never used by the desktop app.</summary>
public sealed class CatalogBundler(HttpClient client)
{
    public async Task<CatalogSnapshot> BundleAsync(string databasePath, string spriteDirectory, CancellationToken cancellationToken = default)
    {
        var importer = new WikiImporter(new BatomonWikiSource(client));
        var result = await importer.ImportAsync(cancellationToken);
        var snapshot = result.Snapshot ?? throw new InvalidDataException(string.Join(" ", result.Errors));

        Directory.CreateDirectory(spriteDirectory);
        foreach (var monster in snapshot.Monsters)
        {
            var destination = Path.Combine(spriteDirectory, $"{monster.Id}.png");
            byte[] sprite;
            try
            {
                sprite = await client.GetByteArrayAsync($"https://batomon.com/game/{snapshot.DeclaredSteamBuild}/monster/{monster.Id}.png", cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                try
                {
                    sprite = await client.GetByteArrayAsync($"https://batomon.com/game/{snapshot.DeclaredSteamBuild}/monster/{monster.Id.Replace('-', '_')}.png", cancellationToken);
                }
                catch (HttpRequestException replacementException) when (replacementException.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new InvalidDataException($"The wiki did not provide a sprite for '{monster.Id}'.", replacementException);
                }
            }
            await File.WriteAllBytesAsync(destination, sprite, cancellationToken);
        }

        if (File.Exists(databasePath)) File.Delete(databasePath);
        var repository = new SnapshotRepository(databasePath);
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }
}
