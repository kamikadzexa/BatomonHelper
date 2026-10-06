using BatomonHelper.Core;
using System.Text.Json;

try
{
    var root = FindRepositoryRoot(Environment.CurrentDirectory, AppContext.BaseDirectory);
    var database = Path.Combine(root, "assets", "wiki", "batomon-wiki.db");
    var sprites = Path.Combine(root, "assets", "wiki");
    var patch = JsonSerializer.Deserialize<CatalogPatch>(await File.ReadAllTextAsync(Path.Combine(root, "assets", "catalog-patches", "1.3.0.json")))
        ?? throw new InvalidDataException("Missing catalog patch.");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BatomonHelper catalog bundler");
    CatalogSnapshot snapshot;
    if (args.Contains("--apply-patch"))
    {
        var existing = await new SnapshotRepository(database, true).GetLatestAsync() ?? throw new InvalidDataException("No bundled wiki snapshot.");
        snapshot = CatalogPatches.Apply(existing with { WikiVersion = existing.WikiVersion ?? "1.2.0" }, patch);
        var staged = database + ".patch.tmp";
        try
        {
            var repository = new SnapshotRepository(staged);
            await repository.InitializeAsync(); await repository.SaveAsync(snapshot);
            if ((await repository.GetLatestAsync())?.ContentHash != snapshot.ContentHash) throw new InvalidDataException("Patch validation failed.");
            File.Move(staged, database, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    else snapshot = await new CatalogBundler(client).BundleAsync(database, sprites, patch: patch);
    Console.WriteLine($"Bundled {snapshot.Monsters.Length} creatures for Steam build {snapshot.DeclaredSteamBuild}.");
    Console.WriteLine($"Wiki {snapshot.WikiVersion ?? "unknown"} / Balance {snapshot.Balance}; reference overrides {snapshot.ReferencePatchVersion ?? "none"}.");
    foreach (var warning in snapshot.Warnings) Console.WriteLine("Warning: " + warning);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Catalog build failed: {exception.Message}");
    Environment.ExitCode = 1;
}

static string FindRepositoryRoot(params string[] starts)
{
    foreach (var start in starts)
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
    throw new DirectoryNotFoundException("Could not find the repository. Run the catalog bundler from this repository, or launch the executable from its build output within the repository.");
}
