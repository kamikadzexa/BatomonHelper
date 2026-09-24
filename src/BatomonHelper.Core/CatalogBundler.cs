namespace BatomonHelper.Core;

/// <summary>Build-time utility for the committed offline wiki snapshot. It is never used by the desktop app.</summary>
public sealed class CatalogBundler(HttpClient client)
{
    public async Task<CatalogSnapshot> BundleAsync(string databasePath, string assetDirectory, CancellationToken cancellationToken = default)
    {
        var importer = new WikiImporter(new BatomonWikiSource(client));
        var result = await importer.ImportAsync(cancellationToken);
        var snapshot = result.Snapshot ?? throw new InvalidDataException(string.Join(" ", result.Errors));

        if (File.Exists(databasePath)) File.Delete(databasePath);
        var repository = new SnapshotRepository(databasePath);
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveAsync(snapshot, cancellationToken);

        var monsterDirectory = Path.Combine(assetDirectory, "monsters");
        var itemDirectory = Path.Combine(assetDirectory, "items");
        var trinketDirectory = Path.Combine(assetDirectory, "trinkets");
        Directory.CreateDirectory(monsterDirectory);
        Directory.CreateDirectory(itemDirectory);
        Directory.CreateDirectory(trinketDirectory);
        foreach (var monster in snapshot.Monsters)
        {
            await TryDownloadSpriteAsync("monster", monster.Id, Path.Combine(monsterDirectory, $"{monster.Id}.png"), snapshot.DeclaredSteamBuild, cancellationToken);
        }
        foreach (var item in snapshot.Items)
            await TryDownloadSpriteAsync("item", item.Id, Path.Combine(itemDirectory, $"{item.Id}.png"), snapshot.DeclaredSteamBuild, cancellationToken);
        foreach (var trinket in snapshot.Trinkets)
            await TryDownloadSpriteAsync("trinket", trinket.Id, Path.Combine(trinketDirectory, $"{trinket.Id}.png"), snapshot.DeclaredSteamBuild, cancellationToken);

        return snapshot;
    }

    private async Task TryDownloadSpriteAsync(string kind, string id, string destination, string build, CancellationToken cancellationToken)
    {
        try { await File.WriteAllBytesAsync(destination, await client.GetByteArrayAsync($"https://batomon.com/game/{build}/{kind}/{id}.png", cancellationToken), cancellationToken); }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            try { await File.WriteAllBytesAsync(destination, await client.GetByteArrayAsync($"https://batomon.com/game/{build}/{kind}/{id.Replace('-', '_')}.png", cancellationToken), cancellationToken); }
            catch (HttpRequestException replacementException) when (replacementException.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
    }
}
