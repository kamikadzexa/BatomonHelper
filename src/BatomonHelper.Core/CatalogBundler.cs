namespace BatomonHelper.Core;

/// <summary>Build-time utility for the committed offline wiki snapshot. It is never used by the desktop app.</summary>
public sealed class CatalogBundler(HttpClient client)
{
    public async Task<CatalogSnapshot> BundleAsync(string databasePath, string assetDirectory, CancellationToken cancellationToken = default, CatalogPatch? patch = null)
    {
        var importer = new WikiImporter(new BatomonWikiSource(client));
        var result = await importer.ImportAsync(cancellationToken);
        var snapshot = result.Snapshot ?? throw new InvalidDataException(string.Join(" ", result.Errors));
        if (patch is not null) snapshot = CatalogPatches.Apply(snapshot, patch);

        var staging = Path.Combine(assetDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var assets = new List<(string Local, string Url)>();
            foreach (var monster in snapshot.Monsters)
            {
                assets.Add(($"monsters/{monster.Id}.png", monster.SpriteUrl ?? throw new InvalidDataException($"No sprite URL for {monster.Id}.")));
                if (monster.ShinySpriteUrl is not null) assets.Add(($"monsters/{monster.Id}-shiny.png", monster.ShinySpriteUrl));
            }
            foreach (var trainer in snapshot.Trainers)
                assets.Add(($"trainers/{trainer.Id}.png", trainer.SpriteUrl ?? throw new InvalidDataException($"No portrait URL for {trainer.Id}.")));
            foreach (var item in snapshot.Items)
                assets.Add(($"items/{item.Id}.png", await ReferenceSprite("item", "items", item.Id, item.Name)));
            foreach (var trinket in snapshot.Trinkets)
                assets.Add(($"trinkets/{trinket.Id}.png", await ReferenceSprite("trinket", "trinkets", trinket.Id, trinket.Name)));
            using var limiter = new SemaphoreSlim(8);
            await Task.WhenAll(assets.Select(async asset =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    var bytes = await client.GetByteArrayAsync(asset.Url, cancellationToken);
                    PngAssetValidation.Validate(bytes, asset.Url);
                    var path = Path.Combine(staging, asset.Local);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                }
                finally { limiter.Release(); }
            }));
            var missing = snapshot.Monsters.Where(monster => monster.ShinySpriteUrl is null).Select(monster => $"No shiny sprite declared for {monster.Id}; shiny status may require review.");
            var manifest = string.Join('\n', assets.OrderBy(asset => asset.Local).Select(asset => asset.Local + ":" +
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(staging, asset.Local))))));
            snapshot = snapshot with { Warnings = snapshot.Warnings.AddRange(missing), ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(snapshot) + manifest))) };
            var stagedDatabase = Path.Combine(staging, "batomon-wiki.db");
            var repository = new SnapshotRepository(stagedDatabase);
            await repository.InitializeAsync(cancellationToken);
            await repository.SaveAsync(snapshot, cancellationToken);
            var loaded = await repository.GetLatestAsync(cancellationToken);
            if (loaded?.Monsters.Length != snapshot.Monsters.Length || loaded.Trainers.IsEmpty) throw new InvalidDataException("Staged catalog validation failed.");
            var replaced = new List<(string Destination, string? Backup)>();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var asset in assets)
                {
                    var destination = Path.Combine(assetDirectory, asset.Local);
                    string? backup = null;
                    if (File.Exists(destination))
                    {
                        backup = Path.Combine(staging, "backup", asset.Local);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(destination, backup);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    replaced.Add((destination, backup));
                    File.Move(Path.Combine(staging, asset.Local), destination, true);
                }
                File.Move(stagedDatabase, databasePath, true);
            }
            catch
            {
                foreach (var entry in replaced.AsEnumerable().Reverse())
                    if (entry.Backup is not null) File.Copy(entry.Backup, entry.Destination, true); else if (File.Exists(entry.Destination)) File.Delete(entry.Destination);
                throw;
            }
            return snapshot;
        }
        finally { Directory.Delete(staging, true); }

        async Task<string> ReferenceSprite(string kind, string category, string id, string name)
        {
            var html = await client.GetStringAsync($"https://batomon.com/{category}/{id}", cancellationToken);
            return WikiImporter.FindSpriteUrl(html, kind, name) ?? throw new InvalidDataException($"No sprite URL for {category}/{id}.");
        }
    }

}
