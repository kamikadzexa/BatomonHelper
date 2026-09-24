using BatomonHelper.Core;

var root = FindRepositoryRoot(Environment.CurrentDirectory);
var database = Path.Combine(root, "assets", "wiki", "batomon-wiki.db");
var sprites = Path.Combine(root, "assets", "wiki", "monsters");
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
client.DefaultRequestHeaders.UserAgent.ParseAdd("BatomonHelper catalog bundler");
var snapshot = await new CatalogBundler(client).BundleAsync(database, sprites);
Console.WriteLine($"Bundled {snapshot.Monsters.Length} creatures for Steam build {snapshot.DeclaredSteamBuild}.");

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run the catalog bundler from this repository or one of its child directories.");
}
