using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace BatomonHelper.Capture;

public sealed record RecognizedValue(string Field, int? Value, double Confidence, bool IsValid, string Status);
public sealed record ScreenValueReadResult(ImmutableDictionary<string, RecognizedValue> Values, string? Diagnostic)
{
    public bool Succeeded => string.IsNullOrEmpty(Diagnostic);
}

public interface IScreenValueReader
{
    Task<ScreenValueReadResult> ReadAsync(int processId, CancellationToken cancellationToken = default);
}

public static class ScreenValueParser
{
    private static readonly (string Field, Regex Pattern)[] Patterns =
    [
        ("Day", new(@"\bday\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("ShopRank", new(@"\b(?:shop\s*)?(?:rank|tier|level)\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Gold", new(@"(?:\b(?:gold|coins?)\s*[:#-]?|\$\s*)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Wins", new(@"\bwins?\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Lives", new(@"\b(?:lives?|health|hp)\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("FreeRerolls", new(@"\b(?:free\s*)?rerolls?\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("IsShopLocked", new(@"\b(?:shop\s*)?lock(?:ed)?\s*[:#-]?\s*(?:([01])|(on|off|yes|no))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("TeamCount", new(@"\b(?:team|board)\s*(?:count)?\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("BenchCount", new(@"\bbench\s*(?:count)?\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("ShopCount", new(@"\b(?:shop|offers?)\s*(?:count)?\s*[:#-]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    public static ImmutableDictionary<string, RecognizedValue> Parse(IEnumerable<(string Text, double Confidence)> lines)
    {
        var candidates = new Dictionary<string, RecognizedValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (text, confidence) in lines)
        foreach (var (field, pattern) in Patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            int? value = field == "IsShopLocked" ? ParseBoolean(match) : int.Parse(match.Groups[1].Value);
            var valid = value is not null && IsValid(field, value.Value);
            var result = new RecognizedValue(field, value, confidence, valid, valid ? (confidence >= 0.65 ? "Recognized" : "Needs confirmation") : "Needs confirmation");
            if (!candidates.TryGetValue(field, out var current) || result.Confidence > current.Confidence) candidates[field] = result;
        }
        return candidates.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static int? ParseBoolean(Match match)
    {
        if (match.Groups[1].Success) return int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToLowerInvariant() switch { "on" or "yes" => 1, "off" or "no" => 0, _ => null };
    }

    public static bool IsValid(string field, int value) => field switch
    {
        "Day" => value is >= 1 and <= 100,
        "ShopRank" => value is >= 1 and <= 10,
        "Gold" => value is >= 0 and <= 999,
        "Wins" or "Lives" or "FreeRerolls" => value is >= 0 and <= 99,
        "IsShopLocked" or "IsOverflow" => value is 0 or 1,
        "TeamCount" or "BenchCount" => value is >= 0 and <= 20,
        "ShopCount" => value is >= 0 and <= 5,
        _ => value >= 0
    };
}
