using System.Drawing;
using System.IO;
using System.Text.Json;

namespace BatomonHelper.ScreenReader;

/// <summary>
/// Optional fixture-backed matcher for sprites that intentionally have no text in the shop UI.
/// The manifest is shipped beside the app when a fixture corpus is supplied.
/// </summary>
public sealed record ReferenceTemplate(string Id, string Kind, string Hash);

public sealed class ReferenceTemplateMatcher
{
    private readonly IReadOnlyList<ReferenceTemplate> _templates;

    public ReferenceTemplateMatcher(string? manifestPath = null)
    {
        manifestPath ??= Path.Combine(AppContext.BaseDirectory, "reference-templates.json");
        if (!File.Exists(manifestPath)) { _templates = []; return; }
        try { _templates = JsonSerializer.Deserialize<List<ReferenceTemplate>>(File.ReadAllText(manifestPath)) ?? []; }
        catch (JsonException) { _templates = []; }
    }

    public RecognitionCandidate? Match(Bitmap image, string kind)
    {
        var hash = DifferenceHash(image);
        var best = _templates.Where(template => template.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Select(template => (Template: template, Distance: HammingDistance(hash, template.Hash)))
            .OrderBy(candidate => candidate.Distance).FirstOrDefault();
        if (best.Template is null) return null;
        var confidence = Math.Max(0d, 1d - best.Distance / 64d);
        return confidence >= .78 ? new(best.Template.Id, confidence) : null;
    }

    public static string DifferenceHash(Bitmap image)
    {
        const int width = 9;
        const int height = 8;
        using var reduced = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(reduced)) graphics.DrawImage(image, new Rectangle(0, 0, width, height));
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width - 1; x++)
        {
            var left = Brightness(reduced.GetPixel(x, y));
            var right = Brightness(reduced.GetPixel(x + 1, y));
            if (left > right) hash |= 1UL << bit;
            bit++;
        }
        return hash.ToString("X16");
    }

    private static int Brightness(Color color) => color.R * 299 + color.G * 587 + color.B * 114;
    private static int HammingDistance(string left, string right)
    {
        if (!ulong.TryParse(left, System.Globalization.NumberStyles.HexNumber, null, out var first) ||
            !ulong.TryParse(right, System.Globalization.NumberStyles.HexNumber, null, out var second)) return 64;
        return System.Numerics.BitOperations.PopCount(first ^ second);
    }
}
