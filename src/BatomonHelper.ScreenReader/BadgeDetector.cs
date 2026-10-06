using BatomonHelper.Core;
using System.Drawing;

namespace BatomonHelper.ScreenReader;

internal static class BadgeDetector
{
    internal readonly record struct Badge(Rectangle Bounds, VisibleStatKind Kind);
    private readonly record struct Face(Rectangle Bounds, VisibleStatKind Kind);

    // Remove panel whites, group complete ink sequences and require a supporting
    // colored badge face. Disconnected strokes must never define separate crops.
    public static IReadOnlyList<Badge> Find(PixelImage image, double scale)
    {
        // Map colored faces independently of all white strokes. Ink groups only
        // associate a sequence with its face; they do not own the face boundary.
        var faces = FindFaces(image);
        var isolated = IsolateDigits(image);
        var ink = isolated.Mask(c => PixelDigits.Ink(c, "light"));
        var runs = new List<(int Left, int Right)>(); var begin = -1;
        for (var x = 0; x <= image.Width; x++)
        {
            var used = x < image.Width && Enumerable.Range(0, image.Height).Any(y => ink[y * image.Width + x]);
            if (used && begin < 0) begin = x;
            if (used || begin < 0) continue;
            if (runs.Count > 0 && begin - runs[^1].Right <= Math.Ceiling(scale * 2)) runs[^1] = (runs[^1].Left, x);
            else runs.Add((begin, x));
            begin = -1;
        }
        var badges = new List<Badge>();
        foreach (var run in runs)
        {
            var top = image.Height; var bottom = -1;
            for (var x = run.Left; x < run.Right; x++) for (var y = 0; y < image.Height; y++)
                if (ink[y * image.Width + x]) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            if (bottom - top < scale * 3) continue;
            // A surrounding colored face must support the entire glyph group. All
            // strokes survive the border flood, including short disconnected pieces.
            var padding = (int)Math.Ceiling(scale * 2);
            var surround = Rectangle.FromLTRB(Math.Max(0, run.Left - padding), Math.Max(0, top - padding),
                Math.Min(image.Width, run.Right + padding), Math.Min(image.Height, bottom + padding + 1));
            var colors = new int[8];
            for (var y = surround.Top; y < surround.Bottom; y++) for (var x = surround.Left; x < surround.Right; x++)
            {
                var colorKind = WindowsBoardScreenReader.BadgeKind(image.At(x, y));
                if (colorKind != VisibleStatKind.Unknown) colors[(int)colorKind]++;
            }
            if (colors.Max() < scale * scale) continue;
            var kind = (VisibleStatKind)Array.IndexOf(colors, colors.Max());
            var bounds = Rectangle.FromLTRB(run.Left, top, run.Right, bottom + 1);
            foreach (var face in faces.Where(face => face.Kind == kind && face.Bounds.IntersectsWith(surround) &&
                         face.Bounds.Width <= run.Right - run.Left + scale * 10))
                bounds = Rectangle.Union(bounds, face.Bounds);
            // Include the dark outline, whose width follows the capture scale.
            bounds.Inflate((int)Math.Ceiling(scale * .7), (int)Math.Ceiling(scale * .7));
            bounds.Intersect(new(0, 0, image.Width, image.Height));
            badges.Add(new(bounds, kind));
        }
        return badges;
    }

    private static IReadOnlyList<Face> FindFaces(PixelImage image)
    {
        var colors = new VisibleStatKind[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
            colors[y * image.Width + x] = WindowsBoardScreenReader.BadgeKind(image.At(x, y));
        var seen = new bool[colors.Length]; var queue = new Queue<int>(); var faces = new List<Face>();
        for (var seed = 0; seed < colors.Length; seed++)
        {
            if (seen[seed] || colors[seed] == VisibleStatKind.Unknown) continue;
            var kind = colors[seed]; var left = image.Width; var right = 0; var top = image.Height; var bottom = 0;
            queue.Enqueue(seed); seen[seed] = true;
            while (queue.TryDequeue(out var p))
            {
                var x = p % image.Width; var y = p / image.Width;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) continue;
                    var next = ny * image.Width + nx;
                    if (!seen[next] && colors[next] == kind) { seen[next] = true; queue.Enqueue(next); }
                }
            }
            faces.Add(new(Rectangle.FromLTRB(left, top, right + 1, bottom + 1), kind));
        }
        return faces;
    }

    public static PixelImage IsolateDigits(PixelImage image)
    {
        var white = image.Mask(c => PixelDigits.Ink(c, "light"));
        var queue = new Queue<int>();
        for (var x = 0; x < image.Width; x++) { Add(x); Add((image.Height - 1) * image.Width + x); }
        for (var y = 0; y < image.Height; y++) { Add(y * image.Width); Add(y * image.Width + image.Width - 1); }
        while (queue.TryDequeue(out var p))
        {
            var x = p % image.Width; var y = p / image.Width;
            if (x > 0) Add(p - 1); if (x + 1 < image.Width) Add(p + 1);
            if (y > 0) Add(p - image.Width); if (y + 1 < image.Height) Add(p + image.Width);
        }
        var bytes = new byte[image.Bytes.Length];
        for (var p = 0; p < white.Length; p++)
        { var value = white[p] ? (byte)255 : (byte)0; bytes[p * 4] = bytes[p * 4 + 1] = bytes[p * 4 + 2] = value; bytes[p * 4 + 3] = 255; }
        return new(image.Width, image.Height, bytes);
        void Add(int p) { if (white[p]) { white[p] = false; queue.Enqueue(p); } }
    }
}
