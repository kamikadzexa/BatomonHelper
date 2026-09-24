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
    public static readonly string[] RequiredPages = ["/", "/batomon", "/mechanics/monster-pool", "/trainers", "/items"];

    public async Task<WikiImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pages = await Task.WhenAll(RequiredPages.Select(path => source.GetStringAsync(path, cancellationToken)));
            var build = FindBuild(pages) ?? throw new InvalidDataException("The wiki snapshot did not declare a Steam build.");
            var monsterIds = MonsterIdRegex().Matches(pages[1]).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (monsterIds.Length == 0) throw new InvalidDataException("The wiki catalog did not contain any Batomon links.");

            using var limiter = new SemaphoreSlim(8);
            var monsters = await Task.WhenAll(monsterIds.Select(async id =>
            {
                await limiter.WaitAsync(cancellationToken);
                try { return ParseMonster(id, await source.GetStringAsync($"/batomon/{id}", cancellationToken)); }
                finally { limiter.Release(); }
            }));

            var valid = monsters.Where(m => m is not null).Cast<MonsterDefinition>().ToImmutableArray();
            if (valid.Length != monsterIds.Length) throw new InvalidDataException("At least one catalog entry was incomplete.");
            var trainers = ParseReferences<TrainerDefinition>(pages[3], TrainerIdRegex, "trainer");
            var items = ParseReferences<ItemDefinition>(pages[4], ItemIdRegex, "item");
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

    private static MonsterDefinition? ParseMonster(string id, string html)
    {
        var rarity = Enum.GetValues<Rarity>().FirstOrDefault(r => Regex.IsMatch(html, $@"\b{Regex.Escape(DisplayRarity(r))}\b", RegexOptions.IgnoreCase));
        if (!Regex.IsMatch(html, @"\b(Common|Uncommon|Rare|Super Rare|Legendary|Mythical)\b", RegexOptions.IgnoreCase)) return null;
        var name = TitleRegex().Match(html).Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name)) name = string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
        var activeSets = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (Regex.IsMatch(html, @"\bStarter\b", RegexOptions.IgnoreCase)) activeSets.Add("starter");
        var baseMatch = BaseSpeciesRegex().Match(html);
        var baseId = baseMatch.Success ? baseMatch.Groups["id"].Value : null;
        return new(id, WebUtility.HtmlDecode(name).Trim(), rarity, baseId, activeSets.ToImmutable(), !html.Contains("no shiny", StringComparison.OrdinalIgnoreCase));
    }

    private static ImmutableArray<TDefinition> ParseReferences<TDefinition>(string html, Func<Regex> matcher, string fallbackKind)
        where TDefinition : class
    {
        var entries = matcher().Matches(html)
            .Select(match => (Id: match.Groups["id"].Value, Name: StripTags(WebUtility.HtmlDecode(match.Groups["name"].Value)).Trim()))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(entry => string.IsNullOrWhiteSpace(entry.Name)
                ? (Id: entry.Id, Name: string.Join(' ', entry.Id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])))
                : entry)
            .ToArray();

        return entries.Select(entry => fallbackKind switch
        {
            "trainer" => (TDefinition)(object)new TrainerDefinition(entry.Id, entry.Name),
            "item" => (TDefinition)(object)new ItemDefinition(entry.Id, entry.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(fallbackKind))
        }).ToImmutableArray();
    }

    private static string? FindBuild(IEnumerable<string> pages) => pages.Select(page => BuildRegex().Match(page)).FirstOrDefault(m => m?.Success == true)?.Groups["build"].Value;
    private static string? FindBalance(IEnumerable<string> pages) => pages.Select(page => BalanceRegex().Match(page)).FirstOrDefault(m => m?.Success == true)?.Groups["balance"].Value;
    private static string DisplayRarity(Rarity rarity) => rarity == Rarity.SuperRare ? "Super Rare" : rarity.ToString();
    private static string StripTags(string value) => TagRegex().Replace(value, string.Empty);

    [GeneratedRegex("href=\\\"/batomon/(?<id>[a-z0-9-]+)\\\"", RegexOptions.IgnoreCase)] private static partial Regex MonsterIdRegex();
    [GeneratedRegex("href=\\\"/trainers/(?<id>[a-z0-9-]+)\\\"[^>]*>(?<name>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TrainerIdRegex();
    [GeneratedRegex("href=\\\"/items/(?<id>[a-z0-9-]+)\\\"[^>]*>(?<name>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ItemIdRegex();
    [GeneratedRegex("<h1[^>]*>(?<name>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleRegex();
    [GeneratedRegex("Steam build\\s*(?:<!--.*?-->\\s*)?(?<build>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BuildRegex();
    [GeneratedRegex("Balance\\s*(?:<!--.*?-->\\s*)?(?<balance>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BalanceRegex();
    [GeneratedRegex("base(?: species| form)?.{0,160}?href=\\\"/batomon/(?<id>[a-z0-9-]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BaseSpeciesRegex();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
}
