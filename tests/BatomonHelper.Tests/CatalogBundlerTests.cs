using BatomonHelper.Core;
using System.Net;
using System.Net.Http;
using System.Text;

namespace BatomonHelper.Tests;

public sealed class CatalogBundlerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Validates_actual_asset_urls_before_replacing_the_bundle(bool missingShiny, bool corruptPng)
    {
        var directory = Path.Combine(Path.GetTempPath(), "batomon-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.Combine(directory, "monsters"));
        var database = Path.Combine(directory, "batomon-wiki.db"); var normal = Path.Combine(directory, "monsters", "alpha.png");
        await File.WriteAllTextAsync(database, "previous database"); await File.WriteAllTextAsync(normal, "previous sprite");
        try
        {
            var pages = new Dictionary<string, string>
            {
                ["/"] = "Balance 24 Steam build 25600878",
                ["/batomon"] = "<a href=\"/batomon/alpha\"><div data-rarity=\"common\"></div></a>",
                ["/mechanics/monster-pool"] = "shop", ["/trainers"] = "<a href=\"/trainers/fighter\">Fighter</a>",
                ["/items"] = "", ["/trinkets"] = "", ["/batomon/regions/jinto"] = "", ["/batomon/regions/pantra"] = "",
                ["/batomon/alpha"] = "<main><h1>Alpha</h1><p>Common Starter</p><img alt=\"Alpha sprite\" src=\"/game/25600878/monster/internal_001.png\"><script>\"/game/25600878/monster/other_filename_shiny.png\"</script></main>",
                ["/trainers/fighter"] = "<main><h1>Fighter</h1><img alt=\"Fighter sprite\" src=\"/game/25600878/trainer/portrait_02.png\"></main>"
            };
            var png = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "assets", "wiki", "monsters", "joltail.png"));
            if (corruptPng) png[20] ^= 1;
            using var client = new HttpClient(new FixtureHandler(pages, png, missingShiny));
            var bundler = new CatalogBundler(client);
            if (missingShiny || corruptPng)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => bundler.BundleAsync(database, directory));
                Assert.Equal("previous database", await File.ReadAllTextAsync(database));
                Assert.Equal("previous sprite", await File.ReadAllTextAsync(normal));
            }
            else
            {
                var catalog = await bundler.BundleAsync(database, directory);
                var monster = Assert.Single(catalog.Monsters);
                Assert.EndsWith("internal_001.png", monster.SpriteUrl); Assert.EndsWith("other_filename_shiny.png", monster.ShinySpriteUrl);
                Assert.EndsWith("portrait_02.png", Assert.Single(catalog.Trainers).SpriteUrl);
                Assert.Equal(png, await File.ReadAllBytesAsync(normal));
                Assert.True(File.Exists(Path.Combine(directory, "monsters", "alpha-shiny.png")));
                Assert.NotNull(await new SnapshotRepository(database, true).GetLatestAsync());
            }
            Assert.Empty(Directory.EnumerateDirectories(directory, ".staging-*"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Every_declared_offline_asset_exists_including_portraits_and_shiny_art()
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        Assert.Equal("25600878", catalog.DeclaredSteamBuild); Assert.Equal("24", catalog.Balance);
        Assert.Equal(24, catalog.Trainers.Length); Assert.Equal(144, catalog.Monsters.Count(monster => monster.ShinySpritePath is not null));
        var paths = catalog.Monsters.SelectMany(monster => new[] { monster.SpritePath, monster.ShinySpritePath })
            .Concat(catalog.Trainers.Select(trainer => trainer.SpritePath)).Concat(catalog.Items.Select(item => item.SpritePath));
        foreach (var path in paths.Where(path => path is not null)) Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, path!)), path);
        Assert.Equal(5, catalog.Warnings.Count(warning => warning.StartsWith("No shiny sprite declared", StringComparison.Ordinal)));
    }

    private sealed class FixtureHandler(IReadOnlyDictionary<string, string> pages, byte[] png, bool missingShiny) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = pages.TryGetValue(path, out var html) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8) } :
                missingShiny && path.EndsWith("_shiny.png", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            return Task.FromResult(response);
        }
    }
}
