using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class CatalogPatchTests
{
    private static CatalogPatch Patch() => JsonSerializer.Deserialize<CatalogPatch>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "1.3.0.json")))!;

    [Fact]
    public async Task Bundled_patch_contains_all_eighteen_changes_and_preserves_wiki_identity()
    {
        var patch = Patch();
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var updated = CatalogPatches.Apply(catalog, patch);
        Assert.Equal(updated.ContentHash, CatalogPatches.Apply(updated, patch).ContentHash);
        Assert.Equal(18, patch.Changes.Length); Assert.Equal(15, patch.Changes.Select(change => change.MonsterId).Distinct().Count());
        Assert.Equal(catalog.DeclaredSteamBuild, updated.DeclaredSteamBuild); Assert.Equal(catalog.Balance, updated.Balance);
        Assert.Equal("1.3.0", updated.ReferencePatchVersion); Assert.Contains(patch.SourceUrl, updated.SourceUrls);
        foreach (var change in patch.Changes)
        {
            var monster = updated.Monsters.Single(monster => monster.Id == change.MonsterId);
            Assert.Equal(change.Value, monster.ReferenceValues[change.Property]);
            Assert.Contains(monster.ReferenceNotes, note => note.Contains(change.PreviousValue + " → " + change.Value));
        }
        Assert.Equal(catalog.Monsters.Select(monster => (monster.Id,monster.Rarity,monster.ActiveSets)), updated.Monsters.Select(monster => (monster.Id,monster.Rarity,monster.ActiveSets)));
    }

    [Fact]
    public async Task Conflicts_unknown_entities_and_changed_baselines_reject_the_patch()
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var patch = Patch();
        Assert.Throws<InvalidDataException>(() => CatalogPatches.Apply(catalog, patch with { Changes = [patch.Changes[0] with { MonsterId = "missing" }] }));
        Assert.Throws<InvalidDataException>(() => CatalogPatches.Apply(catalog with { DeclaredSteamBuild = "changed" }, patch));
        var conflict = catalog with { Monsters = catalog.Monsters.Select(monster => monster.Id == "sarudo" ? monster with { ReferenceValues = monster.ReferenceValues.SetItem("Cost","999") } : monster).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => CatalogPatches.Apply(conflict, patch));
        Assert.Throws<InvalidDataException>(() => CatalogPatches.Apply(catalog, patch with { Changes = [patch.Changes[0],patch.Changes[0]] }));
    }

    [Fact]
    public async Task Updated_wiki_values_retire_overrides_and_older_payloads_remain_readable()
    {
        var patch = Patch();
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var wiki = catalog with { ReferencePatchVersion = null, DeclaredSteamBuild = "newer", WikiVersion = "1.3.0",
            Monsters = catalog.Monsters.Select(monster => monster with { ReferenceNotes = [] }).ToImmutableArray() };
        Assert.Same(wiki, CatalogPatches.Apply(wiki, patch));
        var old = JsonSerializer.Deserialize<CatalogSnapshot>("""
            {"SnapshotId":"old","DeclaredSteamBuild":"old","Balance":null,"RetrievedAt":"2026-01-01T00:00:00Z","SourceUrls":[],"ContentHash":"old","Warnings":[],"Monsters":[{"Id":"old","Name":"Old","Rarity":0,"BaseSpeciesId":null,"ActiveSets":[]}]}
            """)!;
        Assert.Null(old.ReferencePatchVersion); Assert.Empty(old.Monsters[0].ReferenceValues); Assert.Empty(old.Monsters[0].ReferenceNotes);
    }

    [Theory]
    [InlineData("galvanade","Electranade")]
    [InlineData("infernade","Pyronade")]
    [InlineData("brawlmantis","Braulmantis")]
    public void Displayed_aliases_keep_canonical_ids(string id,string alias)
    {
        var monster = new MonsterDefinition(id,id,Rarity.Rare,null,[]);
        Assert.True(CreatureNames.Matches(monster,alias)); Assert.False(CreatureNames.Matches(monster,"unrelated")); Assert.Equal(id,monster.Id);
    }
}
