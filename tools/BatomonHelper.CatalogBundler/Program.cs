using BatomonHelper.Core;

try
{
    var root = FindRepositoryRoot(Environment.CurrentDirectory, AppContext.BaseDirectory);
    var database = Path.Combine(root, "assets", "wiki", "batomon-wiki.db");
    var sprites = Path.Combine(root, "assets", "wiki", "monsters");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BatomonHelper catalog bundler");
    var snapshot = await new CatalogBundler(client).BundleAsync(database, sprites);
    Console.WriteLine($"Bundled {snapshot.Monsters.Length} creatures for Steam build {snapshot.DeclaredSteamBuild}.");
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
