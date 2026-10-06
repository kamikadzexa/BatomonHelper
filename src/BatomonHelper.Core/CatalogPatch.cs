using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatomonHelper.Core;

public sealed record CatalogBalanceChange(string MonsterId, string Property, string PreviousValue, string Value);
public sealed record CatalogPatch(string Version, string RequiredWikiBuild, string SourceUrl, ImmutableArray<CatalogBalanceChange> Changes);

/// <summary>Build-time reference corrections. Never a source of captured board values.</summary>
public static class CatalogPatches
{
    public static CatalogSnapshot Apply(CatalogSnapshot catalog, CatalogPatch patch)
    {
        if (!System.Version.TryParse(patch.Version, out _) || string.IsNullOrWhiteSpace(patch.RequiredWikiBuild) ||
            !Uri.TryCreate(patch.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.Host is not ("store.steampowered.com" or "steamcommunity.com") || patch.Changes.IsDefaultOrEmpty)
            throw new InvalidDataException("Patch metadata must include a version, baseline build and official source.");
        if (patch.Changes.Select(change => (change.MonsterId, change.Property)).Distinct().Count() != patch.Changes.Length)
            throw new InvalidDataException("Duplicate balance override.");
        foreach (var change in patch.Changes)
            if (!catalog.Monsters.Any(monster => monster.Id == change.MonsterId) || string.IsNullOrWhiteSpace(change.Property) ||
                string.IsNullOrWhiteSpace(change.PreviousValue) || string.IsNullOrWhiteSpace(change.Value))
                throw new InvalidDataException($"Invalid balance override for {change.MonsterId}.");

        // Once the wiki contains all new values, its reference data supersedes this layer.
        if (patch.Changes.All(change => catalog.Monsters.First(monster => monster.Id == change.MonsterId)
                .ReferenceValues.GetValueOrDefault(change.Property) == change.Value) && catalog.ReferencePatchVersion is null)
            return catalog;
        if (catalog.DeclaredSteamBuild != patch.RequiredWikiBuild)
            throw new InvalidDataException("The wiki build changed; verify or retire the patch overrides before bundling.");
        var monsters = catalog.Monsters.Select(monster =>
        {
            var values = monster.ReferenceValues;
            var notes = ImmutableArray.CreateBuilder<string>();
            foreach (var change in patch.Changes.Where(change => change.MonsterId == monster.Id))
            {
                if (values.TryGetValue(change.Property, out var existing) && existing != change.PreviousValue && existing != change.Value)
                    throw new InvalidDataException($"Wiki value conflicts with patch {patch.Version}: {monster.Id} {change.Property}.");
                values = values.SetItem(change.Property, change.Value);
                notes.Add($"Patch {patch.Version}: {change.Property} {change.PreviousValue} → {change.Value}");
            }
            return notes.Count == 0 ? monster : monster with { ReferenceValues = values, ReferenceNotes = notes.ToImmutable() };
        }).ToImmutableArray();
        var updated = catalog with
        {
            Monsters = monsters, ReferencePatchVersion = patch.Version,
            SourceUrls = catalog.SourceUrls.Append(patch.SourceUrl).Distinct().ToImmutableArray()
        };
        return updated with { ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(updated with { ContentHash = "" })))) };
    }
}
