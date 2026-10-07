using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatomonHelper.Core;

public sealed record KnowledgeReference(string Id, string Title, string SourceUrl, string Text);
public sealed record MechanicEvidence(string Id, string Title, string SourceUrl, string Rules);
public sealed record PairEvidence(string Id, string FirstId, string SecondId, decimal SeenRate, int SeenCount, int TotalCount,
    decimal RoundWinAssociation, int WinCount, string SourceUrl, string ObservationWindow);
public sealed record CompositionEvidence(string Id, string Name, ImmutableArray<string> CoreIds, JsonElement Data,
    string SourceUrl, string ObservationWindow);
public sealed record StrategyKnowledge(int Version, string SteamBuild, string? Balance, string? PatchVersion,
    DateTimeOffset RetrievedAt, ImmutableArray<KnowledgeReference> References)
{
    public ImmutableArray<MechanicEvidence> Mechanics { get; init; } = [];
    public ImmutableArray<PairEvidence> Pairs { get; init; } = [];
    public ImmutableArray<CompositionEvidence> Compositions { get; init; } = [];
    public static StrategyKnowledge Empty { get; } = new(1, "unknown", null, null, default, []);
}

/// <summary>Read data values from Next page payloads without executing their JavaScript.</summary>
public static class WikiPayload
{
    public static string Decode(string html)
    {
        var chunks = Regex.Matches(html, @"self\.__next_f\.push\((\[1,""(?:\\.|[^""\\])*""\])\)");
        if (chunks.Count == 0)
        {
            // Older snapshots wrapped escaped JSON directly in a script, without a Next chunk envelope.
            // Decode that wrapper once; already plain JSON must keep its escaped string contents.
            var legacy = Regex.Match(html, @"<script\b[^>]*>\s*(?<payload>\{\\""[\s\S]*?)\s*</script>", RegexOptions.IgnoreCase);
            if (!legacy.Success) return html;
            return legacy.Groups["payload"].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\/", "/", StringComparison.Ordinal);
        }
        var decoded = new StringBuilder();
        foreach (Match chunk in chunks)
        {
            using var doc = JsonDocument.Parse(chunk.Groups[1].Value);
            decoded.Append(doc.RootElement[1].GetString());
        }
        return decoded.ToString();
    }
    public static JsonElement? FindArray(string html, string key) => FindValue(html, key, '[');
    private static JsonElement? FindValue(string html, string key, char opening, string? id = null)
    {
        var decoded = Decode(html);
        foreach (Match marker in Regex.Matches(decoded, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?=" + Regex.Escape(opening.ToString()) + ")"))
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
    public static JsonElement? FindObject(string html, string key, string? id = null)
    {
        return FindValue(html, key, '{', id);
    }
    public static MonsterRules? MonsterRules(string id, string html)
    {
        var model = FindObject(html, "monster", id);
        if (model is not { } monster) return null;
        var group = monster.TryGetProperty("mergeGroup", out var merge) && merge.ValueKind == JsonValueKind.String ? merge.GetString()! : id;
        return new(Strings(monster, "types"), Strings(monster, "keywords"), Levels("levels"), Levels("shinyLevels"),
            Abilities("ability"), Abilities("shinyAbility"), monster.TryGetProperty("evolution", out var evolution) && evolution.ValueKind != JsonValueKind.Null ? evolution.GetRawText() : null,
            group, "https://batomon.com/batomon/" + id, DateTimeOffset.UtcNow)
        { AbilityTrigger = Trigger("ability"), ShinyAbilityTrigger = Trigger("shinyAbility") };
        string? Trigger(string key) => monster.TryGetProperty(key, out var ability) && ability.ValueKind == JsonValueKind.Object &&
            ability.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.String ? trigger.GetString() : null;
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

    public static string MechanicsRules(string html)
    {
        var exact = Regex.Match(html, @"<h2\b[^>]*>\s*Exact rules\s*</h2>(?<rules>[\s\S]*?)(?=<h2\b|</main>)", RegexOptions.IgnoreCase);
        if (!exact.Success) throw new InvalidDataException("Mechanic page has no exact rules section.");
        var sections = exact.Groups["rules"].Value;
        // Formula, resolution order and worked rules between Exact rules and Current build examples are significant.
        var tail = html[(exact.Index + exact.Length)..];
        var extra = Regex.Match(tail, @"^(?<rules>[\s\S]*?)(?=<h2\b[^>]*>\s*(?:Current build examples|Common questions|Related mechanics)|</main>)", RegexOptions.IgnoreCase);
        if (extra.Success) sections += extra.Groups["rules"].Value;
        return MainText("<main>" + sections + "</main>");
    }

    public static ImmutableArray<PairEvidence> PairRows(string html, CatalogSnapshot catalog, string window)
    {
        var table = Regex.Match(html, @"<tbody\b[^>]*>([\s\S]*?)</tbody>").Groups[1].Value;
        var result = ImmutableArray.CreateBuilder<PairEvidence>();
        foreach (Match row in Regex.Matches(table, @"<tr\b[^>]*>([\s\S]*?)</tr>"))
        {
            var ids = Regex.Matches(row.Value, "data-monster-card=\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace('_', '-')).Distinct().ToArray();
            var meters = Regex.Matches(row.Value, "<meter[^>]*value=\"([^\"]+)\"").Select(m => decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            var counts = Regex.Matches(MainText("<main>" + row.Value + "</main>"), @"([\d,]+) of ([\d,]+) observations")
                .Select(m => (Numerator: int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                    Denominator: int.Parse(m.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture))).ToArray();
            if (ids.Length != 2 || meters.Length != 2 || counts.Length != 2 || ids.Any(id => !catalog.Monsters.Any(m => m.Id == id)))
                throw new InvalidDataException("Incomplete or unknown Batomon pair row.");
            result.Add(new("pairs/" + string.Join('+', ids), ids[0], ids[1], meters[0], counts[0].Numerator, counts[0].Denominator,
                meters[1], counts[1].Numerator, "https://batomon.com/meta/pairs", window));
        }
        if (result.Count == 0) throw new InvalidDataException("No structured pair rows found.");
        return result.ToImmutable();
    }

    public static ImmutableArray<CompositionEvidence> CompositionRows(string html, CatalogSnapshot catalog, string window)
    {
        var rows = FindArray(html, "qualifiedRows") ?? throw new InvalidDataException("Composition payload is missing.");
        return rows.EnumerateArray().Select(row =>
        {
            var normalized = NormalizeIds(row);
            var ids = Strings(normalized, "coreUnitIds");
            if (ids.IsEmpty || ids.Any(id => !catalog.Monsters.Any(m => m.Id == id)) || !row.TryGetProperty("details", out _))
                throw new InvalidDataException("Incomplete composition core or details.");
            return new CompositionEvidence("comps/" + row.GetProperty("id").GetString(), row.GetProperty("name").GetString()!, ids,
                normalized, "https://batomon.com/comps", window);
        }).ToImmutableArray();
    }
    private static JsonElement NormalizeIds(JsonElement element)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        Write(element, null); writer.Flush();
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
        void Write(JsonElement value, string? key)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()) { writer.WritePropertyName(property.Name); Write(property.Value, property.Name); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var child in value.EnumerateArray()) Write(child, key); writer.WriteEndArray();
            }
            else if (value.ValueKind == JsonValueKind.String && key is "id" or "coreUnitIds" or "substituteIds")
                writer.WriteStringValue(value.GetString()!.Replace('_', '-'));
            else value.WriteTo(writer);
        }
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
        if (paths.Count(p => p.StartsWith("/mechanics/")) < 31) throw new InvalidDataException("Wiki mechanics index is incomplete; all 31 mechanics are required.");
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
                return (Reference: new KnowledgeReference(path.Trim('/'), title, "https://batomon.com" + path, text), Html: html);
            }
            finally { gate.Release(); }
        }));
        var pairs = references.Single(r => r.Reference.Id == "meta/pairs");
        var comps = references.Single(r => r.Reference.Id == "comps");
        return new(2, catalog.DeclaredSteamBuild, catalog.Balance, catalog.ReferencePatchVersion, DateTimeOffset.UtcNow,
            references.Select(r => r.Reference).ToImmutableArray())
        {
            Mechanics = references.Where(r => r.Reference.Id.StartsWith("mechanics/")).Select(r =>
                new MechanicEvidence(r.Reference.Id, r.Reference.Title, r.Reference.SourceUrl, WikiPayload.MechanicsRules(r.Html))).ToImmutableArray(),
            Pairs = WikiPayload.PairRows(pairs.Html, catalog, ObservationWindow(pairs.Reference.Text)),
            Compositions = WikiPayload.CompositionRows(comps.Html, catalog, ObservationWindow(comps.Reference.Text))
        };
    }
    public static string ObservationWindow(string text)
    {
        // Responsive filter controls and their date label can occupy a single text line.
        var dates = Regex.Match(text, @"Dates [^\n]+").Value;
        var scope = text.Split('\n').Where(line => line.Contains("last 30 days") || line.StartsWith("Last updated ") ||
            line.Contains("Based on recent public") || line.StartsWith("Refine ") && line != "Refine the field");
        var result = string.Join(" · ", scope.Prepend(dates).Where(line => line.Length > 0));
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Meta page observation window is missing.");
        return result;
    }
}
