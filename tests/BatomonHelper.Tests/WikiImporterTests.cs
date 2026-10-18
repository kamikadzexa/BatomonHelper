using BatomonHelper.Core;

namespace BatomonHelper.Tests;

public sealed class WikiImporterTests
{
    [Theory]
    [InlineData("opalion", "Rock allies triggered", "1", "Trigger 1 random Rock allies. (Except other Opalion)", "Trigger 1 random Rock allies.", "Trigger 1 random Rock allies.")]
    [InlineData("dollhime", "Damage per trinket gained", "50/100/150", "On Trinket Gained +50 Damage permanently.", "On Trinket Gained +100 Damage permanently.", "On Trinket Gained +150 Damage permanently.")]
    [InlineData("guardiant", "Damage per Bug bought", "8/16/24", "After you buy a Bug monster, this gains +8 Damage.", "After you buy a Bug monster, this gains +16 Damage.", "After you buy a Bug monster, this gains +24 Damage.")]
    [InlineData("lignite", "Burn-to-Damage multiplier", "x20/40/60", "Has additional Damage equal to 20 times this monster's Burn.", "Has additional Damage equal to 40 times this monster's Burn.", "Has additional Damage equal to 60 times this monster's Burn.")]
    public async Task Imports_normal_reference_values_from_the_wiki_page_payload(string id, string property, string expected, string first, string second, string third)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { monster = new {
            id, cost = 25, levels = new[] { new { level = 1, cooldown = 5.5 } },
            ability = new { byLevel = new Dictionary<string, string> { ["1"] = first, ["2"] = second, ["3"] = third } },
            shinyAbility = new { byLevel = new Dictionary<string, string> { ["1"] = "Different shiny value 999" } }
        } });
        var pages = new Dictionary<string, string> {
            ["/"] = "Batomon Showdown 1.3.0 Balance 25 Steam build 99999",
            ["/batomon"] = $"<a href=\"/batomon/{id}\"><div data-rarity=\"rare\"></div></a>",
            ["/mechanics/monster-pool"] = "shop", ["/trainers"] = "", ["/items"] = "",
            ["/batomon/regions/jinto"] = "", ["/batomon/regions/pantra"] = "",
            [$"/batomon/{id}"] = $"<nav>Cost 999</nav><main><h1>{id}</h1><script>{payload.Replace("\"", "\\\"")}</script></main>"
        };
        var snapshot = (await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None)).Snapshot!;
        Assert.Equal("1.3.0", snapshot.WikiVersion); Assert.Equal("25", snapshot.Balance);
        var monster = Assert.Single(snapshot.Monsters);
        Assert.Equal("25", monster.ReferenceValues["Cost"]); Assert.Equal("5.5", monster.ReferenceValues["Cooldown"]);
        Assert.Equal(expected, monster.ReferenceValues[property]); Assert.Empty(monster.ReferenceNotes);
    }

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
            ["/batomon/regions/jinto"] = "<div data-monster-card=\"alpha\"></div>",
            ["/batomon/regions/pantra"] = "",
            ["/batomon/alpha"] = "<h1>Alpha</h1><p>Common Starter</p>",
            ["/trainers/black-belt"] = "<h1>Black Belt</h1><h2>Trainer ability</h2><p>Test trainer rule.</p>",
            ["/items/gray-chip"] = "<h1>Gray Chip</h1><p>Potential reward $15</p><h2>Item effect</h2><p>Test item rule.</p><dl><dt>Cost</dt><dd>$0</dd></dl>"
        };
        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.NotNull(result.Snapshot);
        Assert.Equal("25377295", result.Snapshot!.DeclaredSteamBuild);
        Assert.Equal("alpha", Assert.Single(result.Snapshot.Monsters).Id);
        Assert.Contains("Jinto region", Assert.Single(result.Snapshot.Monsters).Regions);
        Assert.Equal("Black Belt", Assert.Single(result.Snapshot.Trainers).Name);
        Assert.Equal("Gray Chip", Assert.Single(result.Snapshot.Items).Name);
        Assert.Equal("Test item rule.", Assert.Single(result.Snapshot.Items).RuleText);
        Assert.Equal(0,Assert.Single(result.Snapshot.Items).Price);
    }

    [Theory]
    [InlineData("Common", Rarity.Common)]
    [InlineData("Uncommon", Rarity.Uncommon)]
    [InlineData("Rare", Rarity.Rare)]
    [InlineData("Super Rare", Rarity.SuperRare)]
    [InlineData("Legendary", Rarity.Legendary)]
    [InlineData("Mythical", Rarity.Mythical)]
    public async Task Reads_the_creature_rarity_from_the_content_after_its_title(string rarityLabel, Rarity expectedRarity)
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "<p>Balance 22 Steam build 25377295</p>",
            ["/batomon"] = "<a href=\"/batomon/alpha\">Alpha</a><script>{\"regions\":[\"Jinto\"],\"id\":\"alpha\"}</script>",
            ["/mechanics/monster-pool"] = "shop", ["/trainers"] = "", ["/items"] = "", ["/batomon/regions/jinto"] = "", ["/batomon/regions/pantra"] = "",
            ["/batomon/alpha"] = $"<title>Alpha — {rarityLabel} Fire Batomon | batomon.com</title><nav>Common Uncommon Rare Super Rare Legendary Mythical</nav><h1>Alpha</h1><p>{rarityLabel} Fire Starter</p>"
        };

        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.Equal(expectedRarity, Assert.Single(result.Snapshot!.Monsters).Rarity);
    }

    [Fact]
    public async Task Imports_rarity_and_regions_from_the_current_catalog_card_and_region_pages()
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "Steam build 25377295", ["/mechanics/monster-pool"] = "shop", ["/trainers"] = "", ["/items"] = "",
            ["/batomon"] = "<a href=\"/batomon/alpha\"><div data-rarity=\"legendary\"></div></a>",
            ["/batomon/regions/jinto"] = "<div data-monster-card=\"alpha\"></div>",
            ["/batomon/regions/pantra"] = "<div data-monster-card=\"alpha\"></div>",
            ["/batomon/alpha"] = "<h1>Alpha</h1>"
        };

        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        var monster = Assert.Single(result.Snapshot!.Monsters);
        Assert.Equal(Rarity.Legendary, monster.Rarity);
        Assert.Equal(["Jinto region", "Pantra region"], monster.Regions.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Region_pages_ignore_the_site_wide_catalog_navigation()
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "Steam build 25377295", ["/mechanics/monster-pool"] = "shop", ["/trainers"] = "", ["/items"] = "",
            ["/batomon"] = "<a href=\"/batomon/alpha\"><div data-rarity=\"common\"></div></a><a href=\"/batomon/beta\"><div data-rarity=\"common\"></div></a>",
            ["/batomon/regions/jinto"] = "<a href=\"/batomon/beta\">Navigation</a><div data-monster-card=\"alpha\"></div>",
            ["/batomon/regions/pantra"] = "<a href=\"/batomon/alpha\">Navigation</a><div data-monster-card=\"beta\"></div>",
            ["/batomon/alpha"] = "<h1>Alpha</h1>", ["/batomon/beta"] = "<h1>Beta</h1>"
        };

        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.Equal(["Jinto region"], result.Snapshot!.Monsters.Single(monster => monster.Id == "alpha").Regions);
        Assert.Equal(["Pantra region"], result.Snapshot.Monsters.Single(monster => monster.Id == "beta").Regions);
    }

    [Fact]
    public async Task Reads_the_base_species_from_the_current_evolves_from_data()
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "Steam build 25377295", ["/mechanics/monster-pool"] = "shop", ["/trainers"] = "", ["/items"] = "",
            ["/batomon"] = "<a href=\"/batomon/evolved\"><div data-rarity=\"common\"></div></a>",
            ["/batomon/regions/jinto"] = "", ["/batomon/regions/pantra"] = "",
            ["/batomon/evolved"] = """<h1>Evolved</h1><script>{\"evolvesFrom\":[{\"id\":\"base\"}]}</script>"""
        };

        var result = await new WikiImporter(new FakeSource(pages)).ImportAsync(CancellationToken.None);

        Assert.Equal("base", Assert.Single(result.Snapshot!.Monsters).BaseSpeciesId);
    }

    [Fact]
    public async Task Rejects_a_catalog_without_a_declared_build()
    {
        var pages = new Dictionary<string, string>
        {
            ["/"] = "<p>No version</p>", ["/batomon"] = "<a href=\"/batomon/alpha\">Alpha</a>", ["/mechanics/monster-pool"] = "shop",
            ["/trainers"] = "", ["/items"] = "", ["/batomon/regions/jinto"] = "", ["/batomon/regions/pantra"] = ""
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
