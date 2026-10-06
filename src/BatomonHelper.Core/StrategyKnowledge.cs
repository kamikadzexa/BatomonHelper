using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatomonHelper.Core;

public sealed record KnowledgeReference(string Id, string Title, string SourceUrl, string Text);
public sealed record StrategyKnowledge(int Version, string SteamBuild, string? Balance, string? PatchVersion,
    DateTimeOffset RetrievedAt, ImmutableArray<KnowledgeReference> References)
{
    public static StrategyKnowledge Empty { get; } = new(1, "unknown", null, null, default, []);
}

/// <summary>Read data values from Next page payloads without executing their JavaScript.</summary>
public static class WikiPayload
{
    public static JsonElement? FindObject(string html, string key, string? id = null)
    {
        var decoded = html.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\/", "/", StringComparison.Ordinal);
        foreach (Match marker in Regex.Matches(decoded, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?=\\{)"))
        {
            try
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(decoded[(marker.Index + marker.Length)..]));
                using var doc = JsonDocument.ParseValue(ref reader);
                if (id is null || doc.RootElement.TryGetProperty("id", out var value) && value.GetString()?.Replace('_', '-') == id)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }
    public static MonsterRules? MonsterRules(string id, string html)
    {
        var model = FindObject(html, "monster", id);
        if (model is not { } monster) return null;
        var group = monster.TryGetProperty("mergeGroup", out var merge) && merge.ValueKind == JsonValueKind.String ? merge.GetString()! : id;
        return new(Strings(monster, "types"), Strings(monster, "keywords"), Levels("levels"), Levels("shinyLevels"),
            Abilities("ability"), Abilities("shinyAbility"), monster.TryGetProperty("evolution", out var evolution) && evolution.ValueKind != JsonValueKind.Null ? evolution.GetRawText() : null,
            group, "https://batomon.com/batomon/" + id, DateTimeOffset.UtcNow);
        ImmutableArray<MonsterLevelRules> Levels(string key)
        {
            if (!monster.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array) return [];
            return values.EnumerateArray().Select(v => new MonsterLevelRules(v.GetProperty("level").GetInt32(),
                Decimal(v, "cooldown"), (int?)Decimal(v, "multicast"),
                v.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Array
                    ? stats.EnumerateArray().Select(s => new VisibleStat(Kind(s.GetProperty("key").GetString()), (int?)Decimal(s, "value"))).ToImmutableArray() : [])).ToImmutableArray();
        }
        ImmutableDictionary<int, string> Abilities(string key)
        {
            if (!monster.TryGetProperty(key, out var ability) || ability.ValueKind != JsonValueKind.Object || !ability.TryGetProperty("byLevel", out var levels)) return ImmutableDictionary<int, string>.Empty;
            return levels.EnumerateObject().Where(p => int.TryParse(p.Name, out _) && p.Value.ValueKind == JsonValueKind.String)
                .ToImmutableDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!);
        }
    }
    private static ImmutableArray<string> Strings(JsonElement v, string key) => v.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToImmutableArray() : [];
    private static decimal? Decimal(JsonElement v, string key) => v.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out var n) ? n : null;
    private static VisibleStatKind Kind(string? key) => Enum.TryParse<VisibleStatKind>(key?.Replace("_", "").Replace("-", ""), true, out var k) ? k : VisibleStatKind.Unknown;
    public static string MainText(string html)
    {
        var main = Regex.Match(html, @"<main\b[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase);
        var content = main.Success ? main.Groups[1].Value : html;
        content = Regex.Replace(content, @"<(script|style|nav)\b[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"</(p|li|h[1-6]|div|section|tr)>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        content = WebUtility.HtmlDecode(Regex.Replace(content, "<[^>]+>", " "));
        return string.Join('\n', content.Split('\n').Select(line => Regex.Replace(line, @"\s+", " ").Trim()).Where(line => line.Length > 0));
    }
}

public sealed class StrategyKnowledgeImporter(IWikiSource source)
{
    public async Task<StrategyKnowledge> ImportAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        var wiki = await source.GetStringAsync("/wiki", cancellationToken);
        var events = await source.GetStringAsync("/events", cancellationToken);
        var paths = Regex.Matches(wiki, "href=\"(?<path>/mechanics/[a-z0-9-]+)\"")
            .Select(m => m.Groups["path"].Value)
            .Concat(Regex.Matches(events, "href=\"(?<path>/events/[a-z0-9-]+)\"").Select(m => m.Groups["path"].Value))
            .Distinct().Order().Concat(["/comps", "/meta", "/meta/pairs", "/meta/batomon", "/meta/trinkets", "/meta/trainers", "/how-to-play"]).ToArray();
        if (paths.Length < 10) throw new InvalidDataException("Wiki mechanics index is incomplete.");
        using var gate = new SemaphoreSlim(4);
        var references = await Task.WhenAll(paths.Select(async path =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var html = await source.GetStringAsync(path, cancellationToken);
                var text = WikiPayload.MainText(html);
                if (text.Length < 80) throw new InvalidDataException("Incomplete strategy page: " + path);
                var title = WebUtility.HtmlDecode(Regex.Match(html, @"<h1\b[^>]*>(.*?)</h1>").Groups[1].Value);
                return new KnowledgeReference(path.Trim('/'), title, "https://batomon.com" + path, text);
            }
            finally { gate.Release(); }
        }));
        return new(1, catalog.DeclaredSteamBuild, catalog.Balance, catalog.ReferencePatchVersion, DateTimeOffset.UtcNow, references.ToImmutableArray());
    }
}
