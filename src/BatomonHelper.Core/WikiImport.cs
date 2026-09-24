using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BatomonHelper.Core;

public interface IWikiSource
{
    Task<string> GetStringAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken);
}

public sealed class BatomonWikiSource(HttpClient client) : IWikiSource
{
    private static readonly Uri BaseUri = new("https://batomon.com/");

    public Task<string> GetStringAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken) =>
        client.GetStringAsync(new Uri(BaseUri, relativeOrAbsoluteUrl), cancellationToken);
}

public sealed record WikiImportResult(CatalogSnapshot? Snapshot, ImmutableArray<string> Errors);

public sealed partial class WikiImporter(IWikiSource source)
{
    public static readonly string[] RequiredPages = ["/", "/batomon", "/mechanics/monster-pool", "/trainers", "/items", "/batomon/regions/jinto", "/batomon/regions/pantra"];

    public async Task<WikiImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pages = await Task.WhenAll(RequiredPages.Select(path => source.GetStringAsync(path, cancellationToken)));
            var build = FindBuild(pages) ?? throw new InvalidDataException("The wiki snapshot did not declare a Steam build.");
            var catalogRarities = ParseCatalogRarities(pages[1]);
            var monsterIds = MonsterIdRegex().Matches(pages[1]).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (monsterIds.Length == 0) throw new InvalidDataException("The wiki catalog did not contain any Batomon links.");
            var monsterRegions = ParseMonsterRegions(pages[1]);
            AddRegionMonsters(monsterRegions, pages[5], "Jinto region");
            AddRegionMonsters(monsterRegions, pages[6], "Pantra region");

            using var limiter = new SemaphoreSlim(8);
            var monsters = await Task.WhenAll(monsterIds.Select(async id =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    var monster = ParseMonster(id, await source.GetStringAsync($"/batomon/{id}", cancellationToken), catalogRarities.GetValueOrDefault(id));
                    return monster is null ? null : monster with
                    {
                        Regions = monsterRegions.GetValueOrDefault(id, ImmutableHashSet<string>.Empty),
                        SpritePath = $"assets/wiki/monsters/{id}.png"
                    };
                }
                finally { limiter.Release(); }
            }));

            var valid = monsters.Where(m => m is not null).Cast<MonsterDefinition>().ToImmutableArray();
            if (valid.Length != monsterIds.Length)
            {
                var incomplete = monsterIds.Zip(monsters).Where(entry => entry.Second is null).Select(entry => entry.First);
                throw new InvalidDataException($"Catalog entries were incomplete: {string.Join(", ", incomplete)}.");
            }
            var trainerIds = ReferenceIdRegex().Matches(pages[3]).Select(match => match.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var itemIds = ItemIdRegex().Matches(pages[4]).Select(match => match.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var trainers = await ImportReferencesAsync(trainerIds, "/trainers/", ParseTrainer, cancellationToken);
            var items = await ImportReferencesAsync(itemIds, "/items/", ParseItem, cancellationToken);
            var warnings = ImmutableArray.CreateBuilder<string>();
            if (valid.Any(m => m.ActiveSets.IsEmpty)) warnings.Add("Some catalog entries have no declared active set and will not be rolled.");
            if (trainers.IsEmpty) warnings.Add("The trainer reference list was unavailable; trainer OCR will require review.");
            if (items.IsEmpty) warnings.Add("The item reference list was unavailable; item OCR will require review.");
            var raw = string.Concat(pages) + string.Join('\n', monsters.Select(m => m?.Id));
            var snapshot = new CatalogSnapshot(
                $"wiki-{build}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", build, FindBalance(pages), DateTimeOffset.UtcNow,
                RequiredPages.Select(x => new Uri(new Uri("https://batomon.com"), x).ToString()).ToImmutableArray(),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), warnings.ToImmutable(), valid)
            {
                Trainers = trainers,
                Items = items
            };
            return new(snapshot, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(null, [$"Wiki import failed: {exception.Message}"]);
        }
    }

    private static MonsterDefinition? ParseMonster(string id, string html, Rarity? catalogRarity = null)
    {
        var rarity = ParseRarity(html) ?? catalogRarity;
        if (rarity is null) return null;
        var name = TitleRegex().Match(html).Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name)) name = string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
        var activeSets = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (Regex.IsMatch(html, @"\bStarter\b", RegexOptions.IgnoreCase)) activeSets.Add("starter");
        var baseMatch = BaseSpeciesRegex().Match(html);
        if (!baseMatch.Success) baseMatch = EvolvesFromRegex().Match(html);
        var baseId = baseMatch.Success ? baseMatch.Groups["id"].Value : null;
        return new(id, WebUtility.HtmlDecode(name).Trim(), rarity.Value, baseId, activeSets.ToImmutable(), !html.Contains("no shiny", StringComparison.OrdinalIgnoreCase));
    }

    private static Rarity? ParseRarity(string html)
    {
        var pageTitle = PageTitleRarityRegex().Match(html);
        if (pageTitle.Success && TryParseRarity(pageTitle.Groups["rarity"].Value, out var metadataRarity)) return metadataRarity;

        var tier = TierRegex().Match(html);
        if (tier.Success && int.TryParse(tier.Groups["tier"].Value, out var tierValue) && tierValue is >= 1 and <= 6) return (Rarity)(tierValue - 1);

        var title = TitleRegex().Match(html);
        var details = title.Success ? html[(title.Index + title.Length)..Math.Min(html.Length, title.Index + title.Length + 1_500)] : html;
        var rarityMatch = RarityRegex().Match(StripTags(WebUtility.HtmlDecode(details)));
        return rarityMatch.Success && TryParseRarity(rarityMatch.Groups["rarity"].Value, out var contentRarity) ? contentRarity : null;
    }

    private static bool TryParseRarity(string value, out Rarity rarity) => Enum.TryParse(value.Replace(" ", string.Empty), true, out rarity);

    private static Dictionary<string, ImmutableHashSet<string>> ParseMonsterRegions(string html) => MonsterRegionRegex().Matches(html)
        .Select(match => (Id: IdRegex().Match(match.Value).Groups["id"].Value, Regions: RegionValueRegex().Matches(match.Value).Select(region => NormalizeRegion(region.Groups["region"].Value)).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)))
        .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().Regions, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, Rarity> ParseCatalogRarities(string html) => CatalogRarityRegex().Matches(html)
        .Select(match => TryParseRarity(match.Groups["rarity"].Value, out var rarity) ? (Id: match.Groups["id"].Value, Rarity: (Rarity?)rarity) : default)
        .Where(entry => entry.Rarity is not null)
        .ToDictionary(entry => entry.Id, entry => entry.Rarity!.Value, StringComparer.OrdinalIgnoreCase);

    private static void AddRegionMonsters(Dictionary<string, ImmutableHashSet<string>> regions, string html, string region)
    {
        foreach (var id in MonsterCardIdRegex().Matches(html).Select(match => match.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
            regions[id] = regions.GetValueOrDefault(id, ImmutableHashSet<string>.Empty).Add(region);
    }

    private async Task<ImmutableArray<TDefinition>> ImportReferencesAsync<TDefinition>(IEnumerable<string> ids, string pathPrefix, Func<string, string, TDefinition> parser, CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(8);
        var entries = await Task.WhenAll(ids.Select(async id =>
        {
            await limiter.WaitAsync(cancellationToken);
            try { return parser(id, await source.GetStringAsync(pathPrefix + id, cancellationToken)); }
            finally { limiter.Release(); }
        }));
        return entries.ToImmutableArray();
    }

    private static TrainerDefinition ParseTrainer(string id, string html) => new(id, ParseReferenceName(id, html), ParseReferenceEffect(html, "Trainer ability"));
    private static ItemDefinition ParseItem(string id, string html) => new(id, ParseReferenceName(id, html), ParseReferenceEffect(html, "Item effect"), ParsePrice(html));
    private static string ParseReferenceName(string id, string html)
    {
        var name = StripTags(WebUtility.HtmlDecode(TitleRegex().Match(html).Groups["name"].Value)).Trim();
        return string.IsNullOrWhiteSpace(name) ? string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])) : name;
    }
    private static string? ParseReferenceEffect(string html, string heading)
    {
        var match = ReferenceEffectRegex().Match(html);
        if (!match.Success || !match.Groups["heading"].Value.Contains(heading, StringComparison.OrdinalIgnoreCase)) return null;
        var text = StripTags(WebUtility.HtmlDecode(match.Groups["effect"].Value)).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    private static int? ParsePrice(string html)
    {
        var match = PriceRegex().Match(StripTags(WebUtility.HtmlDecode(html)));
        return match.Success && int.TryParse(match.Groups["price"].Value, out var price) ? price : null;
    }

    private static string? FindBuild(IEnumerable<string> pages) => pages.Select(page => BuildRegex().Match(page)).FirstOrDefault(m => m?.Success == true)?.Groups["build"].Value;
    private static string? FindBalance(IEnumerable<string> pages) => pages.Select(page => BalanceRegex().Match(page)).FirstOrDefault(m => m?.Success == true)?.Groups["balance"].Value;
    private static string DisplayRarity(Rarity rarity) => rarity == Rarity.SuperRare ? "Super Rare" : rarity.ToString();
    private static string NormalizeRegion(string region) => region.EndsWith(" region", StringComparison.OrdinalIgnoreCase) ? region : $"{region} region";
    private static string StripTags(string value) => TagRegex().Replace(value, string.Empty);

    [GeneratedRegex("href=\\\"/batomon/(?<id>[a-z0-9-]+)\\\"", RegexOptions.IgnoreCase)] private static partial Regex MonsterIdRegex();
    [GeneratedRegex("data-monster-card=\\\"(?<id>[a-z0-9-]+)\\\"", RegexOptions.IgnoreCase)] private static partial Regex MonsterCardIdRegex();
    [GeneratedRegex("<a\\b[^>]*href=\\\"/batomon/(?<id>[a-z0-9-]+)\\\"[^>]*>.*?data-rarity=\\\"(?<rarity>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex CatalogRarityRegex();
    [GeneratedRegex("\\{(?=[^{}]*\\\"id\\\"\\s*:\\s*\\\"[a-z0-9-]+\\\")(?=[^{}]*\\\"regions\\\"\\s*:\\s*\\[[^]]*\\])[^{}]*\\}", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex MonsterRegionRegex();
    [GeneratedRegex("\\\"(?<region>(?:Jinto|Pantra)(?:\\s+region)?)\\\"", RegexOptions.IgnoreCase)] private static partial Regex RegionValueRegex();
    [GeneratedRegex("\\\"id\\\"\\s*:\\s*\\\"(?<id>[a-z0-9-]+)\\\"", RegexOptions.IgnoreCase)] private static partial Regex IdRegex();
    [GeneratedRegex("href\\s*=\\s*['\\\"]/trainers/(?<id>[a-z0-9-]+)['\\\"]", RegexOptions.IgnoreCase)] private static partial Regex ReferenceIdRegex();
    [GeneratedRegex("href\\s*=\\s*['\\\"]/items/(?<id>[a-z0-9-]+)['\\\"]", RegexOptions.IgnoreCase)] private static partial Regex ItemIdRegex();
    [GeneratedRegex("<h1[^>]*>(?<name>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleRegex();
    [GeneratedRegex("<title>[^<]*?[—-]\\s*(?<rarity>Super\\s+Rare|Uncommon|Common|Rare|Legendary|Mythical)\\b", RegexOptions.IgnoreCase)] private static partial Regex PageTitleRarityRegex();
    [GeneratedRegex("\\\"tier\\\"\\s*:\\s*(?<tier>[1-6])", RegexOptions.IgnoreCase)] private static partial Regex TierRegex();
    [GeneratedRegex("\\b(?<rarity>Super\\s+Rare|Uncommon|Common|Rare|Legendary|Mythical)\\b", RegexOptions.IgnoreCase)] private static partial Regex RarityRegex();
    [GeneratedRegex("Steam build\\s*(?:<!--.*?-->\\s*)?(?<build>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BuildRegex();
    [GeneratedRegex("Balance\\s*(?:<!--.*?-->\\s*)?(?<balance>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BalanceRegex();
    [GeneratedRegex("base(?: species| form)?.{0,160}?href=\\\"/batomon/(?<id>[a-z0-9-]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BaseSpeciesRegex();
    [GeneratedRegex(@"\\?""evolvesFrom\\?""\s*:\s*\[\s*\{[^{}]*?\\?""id\\?""\s*:\s*\\?""(?<id>[a-z0-9-]+)\\?""", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex EvolvesFromRegex();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex("\\$(?<price>\\d+)")] private static partial Regex PriceRegex();
    [GeneratedRegex("<h2[^>]*>(?<heading>.*?)</h2>\\s*<p[^>]*>(?<effect>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ReferenceEffectRegex();
}
