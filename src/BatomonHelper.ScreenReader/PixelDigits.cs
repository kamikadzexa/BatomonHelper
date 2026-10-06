using System.Drawing;
using System.Text.Json;
using System.IO;

namespace BatomonHelper.ScreenReader;

internal sealed class PixelDigits
{
    private sealed record Glyph(string Character, string Mask, string Family, double Aspect, int Holes, int Width = 0, int Height = 0, string? NativeMask = null)
    {
        public bool[] Pixels { get; } = Mask.Select(value => value == '1').ToArray();
        public bool[] NativePixels { get; } = NativeMask?.Select(value => value == '1').ToArray() ?? [];
    }
    private readonly Dictionary<string, Glyph[]> _families;
    public PixelDigits(string path) => _families = (File.Exists(path) ? JsonSerializer.Deserialize<Glyph[]>(File.ReadAllText(path)) ?? [] : [])
        .GroupBy(glyph => glyph.Family).ToDictionary(group => group.Key, group => group.ToArray());
    public (string Text, double Confidence, RecognitionCandidate[] Candidates) Read(PixelImage image, string palette = "light", string? family = null)
    {
        family ??= palette is "level" or "silver" ? "level" : palette == "price" ? "price" : palette == "light" ? "badge" : "ribbon";
        var glyphs = _families.GetValueOrDefault(family) ?? [];
        var mask = image.Mask(c => Ink(c, palette));
        var bounds = PixelImage.Bounds(mask, image.Width, image.Height);
        if (bounds.IsEmpty) return (string.Empty, 0, []);
        var text = ""; var confidence = 1d; int start = -1;
        (string Text, double Score)[] alternatives = [("", 1)];
        for (var x = bounds.Left; x <= bounds.Right; x++)
        {
            var used = x < bounds.Right && Enumerable.Range(bounds.Top, bounds.Height).Any(y => mask[y * image.Width + x]);
            if (used && start < 0) start = x;
            if (used || start < 0) continue;
            int top = bounds.Bottom, bottom = bounds.Top;
            for (var gx = start; gx < x; gx++) for (var y = bounds.Top; y < bounds.Bottom; y++) if (mask[y * image.Width + gx]) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            var height = bottom - top + 1;
            if (height < bounds.Height * (family == "level" ? .75 : .40)) { start = -1; continue; }
            var decoded = Decode(start, x, top, bottom + 1);
            text += decoded.Text;
            confidence = Math.Min(confidence, decoded.Confidence);
            alternatives = alternatives.SelectMany(head => decoded.Candidates.Select(tail =>
                (Text: head.Text + tail.Text, Score: Math.Min(head.Score, tail.Score))))
                .OrderByDescending(candidate => candidate.Score).DistinctBy(candidate => candidate.Text).Take(4).ToArray();
            start = -1;
        }
        return (text, text.Length == 0 ? 0 : confidence,
            alternatives.Where(candidate => candidate.Text.Length > 0).Select(candidate => new RecognitionCandidate(candidate.Text, candidate.Score)).ToArray());

        (string Text, double Confidence, (string Text, double Score)[] Candidates) Decode(int left, int right, int top, int bottom)
        {
            var ranked = Rank(left, right, top, bottom);
            if (family != "badge" || right - left <= (bottom - top) * .9)
                return ranked.Length == 0 ? ("", 0, []) : (ranked[0].Text, Resolved(ranked), ranked);
            // Joined glyphs are decoded as a sequence of independently fitted widths.
            // Keep competing strings rather than assuming equal-width characters.
            var paths = new Dictionary<int, (string Text, double Score, double Minimum)[]>();
            var best = Solve(left);
            return best.Length == 0 ? ("", 0, []) : (best[0].Text,
                best[0].Minimum >= .88 && (best.Length == 1 || best[0].Score - best[1].Score >= .035) ? best[0].Minimum : 0,
                best.Select(candidate => (candidate.Text, candidate.Minimum)).ToArray());

            (string Text, double Score, double Minimum)[] Solve(int position)
            {
                if (position == right) return [("", 1, 1)];
                if (paths.TryGetValue(position, out var cached)) return cached;
                var options = new List<(string Text, double Score, double Minimum)>();
                var h = bottom - top;
                for (var end = position + Math.Max(1, (int)(h * .20)); end <= Math.Min(right, position + (int)Math.Ceiling(h * .9)); end++)
                {
                    var entries = Rank(position, end, top, bottom);
                    // A font seam can join a few stroke pixels. Cutting through
                    // a digit's vertical stroke must cost more than that seam.
                    var crossing = end == right ? 0 : Enumerable.Range(top, h)
                        .Count(y => mask[y * image.Width + end - 1] && mask[y * image.Width + end]);
                    var seamPenalty = .15 * crossing / h;
                    // Charge a small width-normalized cost per glyph so a digit
                    // cannot win by being explained as several narrower pieces.
                    foreach (var entry in entries.Where(entry => entry.Score >= .78))
                        foreach (var tail in Solve(end))
                            options.Add((entry.Text + tail.Text,
                                ((entry.Score - seamPenalty) * (end - position) + tail.Score * (right - end) - .035 * h) / (right - position),
                                Math.Min(entry.Score, tail.Minimum)));
                }
                return paths[position] = options.OrderByDescending(option => option.Score).DistinctBy(option => option.Text).Take(2).ToArray();
            }
        }
        static double Resolved((string Text, double Score)[] ranked) => ranked[0].Score >= .88 &&
            (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= .045) ? ranked[0].Score : 0;

        (string Text, double Score)[] Rank(int left, int right, int top, int bottom)
        {
            // Trim a segment without losing any disconnected strokes.
            var actualTop = bottom; var actualBottom = top - 1; var actualLeft = right; var actualRight = left - 1;
            for (var y = top; y < bottom; y++) for (var gx = left; gx < right; gx++)
                if (mask[y * image.Width + gx]) { actualTop = Math.Min(actualTop, y); actualBottom = Math.Max(actualBottom, y); actualLeft = Math.Min(actualLeft, gx); actualRight = Math.Max(actualRight, gx); }
            if (actualRight < actualLeft) return [];
            var w = actualRight - actualLeft + 1; var h = actualBottom - actualTop + 1; var aspect = w / (double)h;
            var normalized = new bool[384];
            for (var y = 0; y < 24; y++) for (var gx = 0; gx < 16; gx++)
                normalized[y * 16 + gx] = mask[(actualTop + Math.Min(h - 1, (int)((y + .5) * h / 24))) * image.Width + actualLeft + Math.Min(w - 1, (int)((gx + .5) * w / 16))];
            var holes = CountHoles(normalized);
            return glyphs.Where(glyph => Math.Abs(Math.Log(aspect / glyph.Aspect)) < .45)
                .Select(glyph => (Text: glyph.Character, Score: glyph.NativePixels.Length == glyph.Width * glyph.Height && glyph.Width > 0
                    ? NativeScore(glyph) - Math.Min(.20, Math.Abs(Math.Log(aspect / glyph.Aspect)) * .25)
                        - Math.Min(.25, Math.Abs(Math.Log(h / (double)glyph.Height)) * .15)
                    : Agreement(normalized, glyph.Pixels) - Math.Min(.20, Math.Abs(Math.Log(aspect / glyph.Aspect)) * .10) - Math.Min(.20, Math.Abs(holes - glyph.Holes) * .12)))
                .GroupBy(entry => entry.Text).Select(group => group.MaxBy(entry => entry.Score)).OrderByDescending(entry => entry.Score).Take(2).ToArray();

            double NativeScore(Glyph glyph)
            {
                int same = 0, intersection = 0, union = 0;
                for (var y = 0; y < h; y++) for (var gx = 0; gx < w; gx++)
                {
                    var a = mask[(actualTop + y) * image.Width + actualLeft + gx];
                    var t = glyph.NativePixels[Math.Min(glyph.Height - 1, (int)((y + .5) * glyph.Height / h)) * glyph.Width + Math.Min(glyph.Width - 1, (int)((gx + .5) * glyph.Width / w))];
                    if (a == t) same++; if (a && t) intersection++; if (a || t) union++;
                }
                return .5 * same / (w * h) + .5 * intersection / Math.Max(1, union);
            }
        }
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
        "silver" => c.R is > 110 and < 220 && c.G > 110 && c.B > c.R + 6 && c.B > c.G + 6 && Math.Abs(c.R - c.G) < 30 && Math.Abs(c.R - c.B) < 65,
        "price" => Ink(c, "light") || (c.R > 200 && c.G < 110 && c.B is > 75 and < 180),
        "gray" => c.R is > 65 and < 155 && Math.Abs(c.R - c.G) < 14 && Math.Abs(c.G - c.B) < 25,
        "green" => c.G > c.R * 1.3 && c.G > c.B * 1.3 && c.G > 100,
        "blue" => c.B > c.R * 1.4 && c.G > c.R * 1.3 && c.B > 100,
        "purple" => c.R > c.G * 1.3 && c.B > c.G * 1.3 && c.R > 100,
        "orange" => c.R > c.G * 1.25 && c.G > c.B * 1.3 && c.R > 100,
        _ => Math.Min(c.R, Math.Min(c.G, c.B)) > 190 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 45
    };
}
