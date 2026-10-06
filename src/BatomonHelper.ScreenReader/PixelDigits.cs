using System.Drawing;
using System.Text.Json;
using System.IO;

namespace BatomonHelper.ScreenReader;

internal sealed class PixelDigits
{
    private sealed record Glyph(string Character, string Mask, string Family, double Aspect, int Holes)
    {
        public bool[] Pixels { get; } = Mask.Select(value => value == '1').ToArray();
    }
    private readonly Dictionary<string, Glyph[]> _families;
    public PixelDigits(string path) => _families = (File.Exists(path) ? JsonSerializer.Deserialize<Glyph[]>(File.ReadAllText(path)) ?? [] : [])
        .GroupBy(glyph => glyph.Family).ToDictionary(group => group.Key, group => group.ToArray());
    public (string Text, double Confidence) Read(PixelImage image, string palette = "light", string? family = null)
    {
        family ??= palette is "level" or "silver" ? "level" : palette == "price" ? "price" : palette == "light" ? "badge" : "ribbon";
        var glyphs = _families.GetValueOrDefault(family) ?? [];
        var mask = image.Mask(c => Ink(c, palette));
        var bounds = PixelImage.Bounds(mask, image.Width, image.Height);
        if (bounds.IsEmpty) return (string.Empty, 0);
        var text = ""; var confidence = 1d; int start = -1;
        for (var x = bounds.Left; x <= bounds.Right; x++)
        {
            var used = x < bounds.Right && Enumerable.Range(bounds.Top, bounds.Height).Any(y => mask[y * image.Width + x]);
            if (used && start < 0) start = x;
            if (used || start < 0) continue;
            int top = bounds.Bottom, bottom = bounds.Top;
            for (var gx = start; gx < x; gx++) for (var y = bounds.Top; y < bounds.Bottom; y++) if (mask[y * image.Width + gx]) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            var height = bottom - top + 1;
            if (height < bounds.Height * .40) { start = -1; continue; }
            var segments = family == "badge" && x - start > height * .9 ? Math.Max(1, (int)Math.Round((x - start) / (height * .6))) : 1;
            var runStart = start;
            for (var segment = 0; segment < segments; segment++)
            {
                var glyphLeft = runStart + (int)Math.Round(segment * (x - runStart) / (double)segments);
                var glyphRight = runStart + (int)Math.Round((segment + 1) * (x - runStart) / (double)segments);
                var normalized = new bool[384];
                for (var y = 0; y < 24; y++) for (var gx = 0; gx < 16; gx++)
                    normalized[y * 16 + gx] = mask[(top + Math.Min(height - 1, (int)((y + .5) * height / 24))) * image.Width + glyphLeft + Math.Min(glyphRight - glyphLeft - 1, (int)((gx + .5) * (glyphRight - glyphLeft) / 16))];
                var holes = CountHoles(normalized);
                var aspect = (glyphRight - glyphLeft) / (double)height;
                var ranked = glyphs.Select(glyph => (glyph.Character, Score: Agreement(normalized, glyph.Pixels)
                    - Math.Min(.20, Math.Abs(Math.Log(aspect / glyph.Aspect)) * .10) - Math.Min(.20, Math.Abs(holes - glyph.Holes) * .12)))
                    .GroupBy(entry => entry.Character).Select(group => group.MaxBy(entry => entry.Score)).OrderByDescending(entry => entry.Score).Take(2).ToArray();
                if (ranked.Length == 0) return (string.Empty, 0);
                text += ranked[0].Character;
                confidence = Math.Min(confidence, ranked[0].Score < .88 || (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < .045) ? 0 : ranked[0].Score);
            }
            start = -1;
        }
        return (text, confidence);
    }
    private static double Agreement(bool[] actual, bool[] template)
    {
        var matched = 0;
        for (var i = 0; i < actual.Length; i++) if (actual[i] == template[i]) matched++;
        return matched / (double)actual.Length;
    }
    private static int CountHoles(bool[] mask)
    {
        var visited = new bool[mask.Length]; var queue = new Queue<int>(); var holes = 0;
        for (var seed = 0; seed < mask.Length; seed++)
        {
            if (mask[seed] || visited[seed]) continue;
            queue.Enqueue(seed); visited[seed] = true; var area = 0; var edge = false;
            while (queue.TryDequeue(out var p))
            {
                area++; var x = p % 16; var y = p / 16; edge |= x == 0 || x == 15 || y == 0 || y == 23;
                Visit(x > 0 ? p - 1 : -1); Visit(x < 15 ? p + 1 : -1); Visit(y > 0 ? p - 16 : -1); Visit(y < 23 ? p + 16 : -1);
            }
            if (!edge && area >= 3) holes++;
            void Visit(int p) { if (p >= 0 && !mask[p] && !visited[p]) { visited[p] = true; queue.Enqueue(p); } }
        }
        return holes;
    }
    internal static bool Ink(Color c, string mode) => mode switch
    {
        "level" => (c.R > 130 && c.G > 75 && c.B < 125 && c.R > c.G * 1.1) ||
            (c.R > 190 && c.G > 180 && c.B < 125) || (c.R > 200 && c.G < 140 && c.B is > 75 and < 180),
        "silver" => c.R is > 110 and < 220 && c.G > 110 && c.B > 130 && Math.Abs(c.R - c.G) < 30 && Math.Abs(c.R - c.B) < 65,
        "price" => Ink(c, "light") || (c.R > 200 && c.G < 110 && c.B is > 75 and < 180),
        "gray" => c.R is > 65 and < 155 && Math.Abs(c.R - c.G) < 14 && Math.Abs(c.G - c.B) < 25,
        "green" => c.G > c.R * 1.3 && c.G > c.B * 1.3 && c.G > 100,
        "blue" => c.B > c.R * 1.4 && c.G > c.R * 1.3 && c.B > 100,
        "purple" => c.R > c.G * 1.3 && c.B > c.G * 1.3 && c.R > 100,
        "orange" => c.R > c.G * 1.25 && c.G > c.B * 1.3 && c.R > 100,
        _ => Math.Min(c.R, Math.Min(c.G, c.B)) > 190 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 45
    };
}
