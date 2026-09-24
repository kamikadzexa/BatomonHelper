using BatomonHelper.Core;

namespace BatomonHelper.Tests;

public sealed class WikiImporterTests
{
    [Fact]
    public async Task Imports_a_patch_pinned_catalog()
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "<p>Balance 22 Steam build <!-- -->25377295</p>",
            ["/batomon"] = "<a href=\"/batomon/alpha\">Alpha</a><script>{\"regions\":[\"Jinto\"],\"id\":\"alpha\"}</script>",
            ["/mechanics/monster-pool"] = "<h1>Shop</h1>",
            ["/trainers"] = "<a class=\"catalog\" href=\"/trainers/black-belt\"><span>Black Belt</span></a>",
            ["/items"] = "<a class=\"catalog\" href=\"/items/gray-chip\"><span>Gray Chip</span></a>",
            ["/batomon/alpha"] = "<h1>Alpha</h1><p>Common Starter</p>",
            ["/trainers/black-belt"] = "<h1>Black Belt</h1><h2>Trainer ability</h2><p>Test trainer rule.</p>",
            ["/items/gray-chip"] = "<h1>Gray Chip</h1><h2>Item effect</h2><p>Test item rule.</p>"
        };
        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.NotNull(result.Snapshot);
        Assert.Equal("25377295", result.Snapshot!.DeclaredSteamBuild);
        Assert.Equal("alpha", Assert.Single(result.Snapshot.Monsters).Id);
        Assert.Contains("Jinto region", Assert.Single(result.Snapshot.Monsters).Regions);
        Assert.Equal("Black Belt", Assert.Single(result.Snapshot.Trainers).Name);
        Assert.Equal("Gray Chip", Assert.Single(result.Snapshot.Items).Name);
        Assert.Equal("Test item rule.", Assert.Single(result.Snapshot.Items).RuleText);
    }

    [Fact]
    public async Task Rejects_a_catalog_without_a_declared_build()
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "<p>No version</p>", ["/batomon"] = "<a href=\"/batomon/alpha\">Alpha</a>", ["/mechanics/monster-pool"] = "shop",
            ["/trainers"] = "", ["/items"] = ""
        };
        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.Null(result.Snapshot);
        Assert.NotEmpty(result.Errors);
    }

    private sealed class FakeSource(IReadOnlyDictionary<string, string> pages) : IWikiSource
    {
        public Task<string> GetStringAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken) => Task.FromResult(pages[relativeOrAbsoluteUrl]);
    }
}
