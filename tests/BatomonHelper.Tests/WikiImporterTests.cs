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
            ["/batomon"] = "<a href=\"/batomon/alpha\">Alpha</a>",
            ["/mechanics/monster-pool"] = "<h1>Shop</h1>",
            ["/trainers"] = "<a href=\"/trainers/black-belt\">Black Belt</a>",
            ["/items"] = "<a href=\"/items/gray-chip\">Gray Chip</a>",
            ["/batomon/alpha"] = "<h1>Alpha</h1><p>Common Starter</p>"
        };
        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.NotNull(result.Snapshot);
        Assert.Equal("25377295", result.Snapshot!.DeclaredSteamBuild);
        Assert.Equal("alpha", Assert.Single(result.Snapshot.Monsters).Id);
        Assert.Equal("Black Belt", Assert.Single(result.Snapshot.Trainers).Name);
        Assert.Equal("Gray Chip", Assert.Single(result.Snapshot.Items).Name);
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
