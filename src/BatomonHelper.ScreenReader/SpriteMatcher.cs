using BatomonHelper.Core;
using System.Drawing;
using System.IO;

namespace BatomonHelper.ScreenReader;

internal sealed record SpriteMatch(string? Id, SlotOccupancy Kind, ShinyState Variant, double Score, double Margin, bool Grayscale, RecognitionCandidate[] Candidates);
internal sealed class SpriteMatcher
{
    private sealed record Template(string Id, ShinyState Variant, SlotOccupancy Occupancy, PixelImage Pixels, double Aspect);
    private readonly record struct ForegroundPoint(int Offset, byte R, byte G, byte B, bool Dark);
    private sealed record NativeTemplate(string Id, ShinyState Variant, SlotOccupancy Kind, ForegroundPoint[] Points);
    private readonly List<NativeTemplate> _native = [];
    private readonly List<Template> _portraits = [];
    private readonly HashSet<string> _shinySpecies;
    public List<string> MissingAssets { get; } = [];
    public SpriteMatcher(CatalogSnapshot catalog, string assetRoot)
    {
        _shinySpecies = catalog.Monsters.Where(monster => monster.HasShiny).Select(monster => monster.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var monster in catalog.Monsters)
        {
            Add(_portraits, monster.Id, ShinyState.Normal, SlotOccupancy.Creature, monster.SpritePath);
            Add(_portraits, monster.Id, ShinyState.Shiny, SlotOccupancy.Creature, monster.ShinySpritePath);
        }
        foreach (var item in catalog.Items) Add(_portraits, item.Id, ShinyState.Unknown, SlotOccupancy.Item, item.SpritePath);
        foreach (var trainer in catalog.Trainers) Add(_portraits, trainer.Id, ShinyState.Unknown, SlotOccupancy.Unknown, trainer.SpritePath, true);
        void Add(List<Template> list, string id, ShinyState variant, SlotOccupancy kind, string? path, bool portrait = false)
        {
            if (path is null) return;
            var resolved = Path.Combine(assetRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(resolved)) { MissingAssets.Add(path); return; }
            using var bitmap = new Bitmap(resolved);
            var pixels = PixelImage.FromBitmap(bitmap);
            if (!portrait)
            {
                var native = pixels;
                if (pixels.Width != 48 || pixels.Height != 48)
                {
                    // Item art uses a 32-pixel canvas at the same pixel scale as creatures.
                    var canvas = new byte[48 * 48 * 4];
                    var left = (48 - pixels.Width) / 2; var top = 48 - pixels.Height;
                    for (var y = 0; y < pixels.Height; y++) Buffer.BlockCopy(pixels.Bytes, y * pixels.Width * 4, canvas, ((y + top) * 48 + left) * 4, pixels.Width * 4);
                    native = new(48, 48, canvas);
                }
                var points = new List<ForegroundPoint>();
                for (var y = 0; y < 48; y++) for (var x = 0; x < 48; x++)
                { var c = native.At(x, y); if (c.A > 128) points.Add(new(y * 48 + x, c.R, c.G, c.B, Math.Max(c.R, Math.Max(c.G, c.B)) < 65)); }
                var step = Math.Max(1, points.Count / 100);
                _native.Add(new(id, variant, kind, points.Where((_, i) => i % step == 0).ToArray()));
                return;
            }
            var bounds = portrait ? new Rectangle(0, 0, pixels.Width, pixels.Height) : PixelImage.Bounds(pixels.Mask(c => c.A > 128), pixels.Width, pixels.Height);
            if (!bounds.IsEmpty)
            {
                var trimmed = pixels.Crop(bounds);
                foreach (var cut in portrait ? new[] { 0d } : new[] { 0d, .07, .14, .21, .28, .35 })
                {
                    var visibleHeight = Math.Max(1, (int)Math.Round(trimmed.Height * (1 - cut)));
                    list.Add(new(id, variant, kind, trimmed.Crop(new Rectangle(0, 0, trimmed.Width, visibleHeight)).Resize(32, 40), trimmed.Width / (double)visibleHeight));
                }
            }
        }
    }
    public (string? Id, double Score, RecognitionCandidate[] Candidates) MatchTrainer(PixelImage image)
    {
        var ranked = Rank(image.Resize(32, 40), image.Width / (double)image.Height, _portraits, false, true);
        return (ranked.FirstOrDefault().Template?.Id, ranked.FirstOrDefault().Score, ranked.Take(3).Select(entry => new RecognitionCandidate(entry.Template.Id, entry.Score)).ToArray());
    }
    public SpriteMatch Match(PixelImage image, double scale)
    {
        var dark = image.Mask(c => c.R < 65 && c.G < 65 && c.B < 65);
        var box = PixelImage.Bounds(dark, image.Width, image.Height);
        if (box.IsEmpty) return new(null, SlotOccupancy.Unknown, ShinyState.Unknown, 0, 0, false, []);
        var foreground = image.Crop(box);
        var colored = 0;
        for (var y = 0; y < foreground.Height; y++) for (var x = 0; x < foreground.Width; x++)
        {
            // Shop lock icons and level labels keep their color on grayscale offers.
            var ix = box.X + x; var iy = box.Y + y;
            if (iy < image.Height * .45 && (ix < image.Width * .45 || ix > image.Width * .60)) continue;
            var c = foreground.At(x, y);
            if (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 35) colored++;
        }
        var grayscale = colored < foreground.Width * foreground.Height * .015;
        var offsets = new int[17 * 3 * 48 * 48];
        for (var dy = -6; dy <= 10; dy++) for (var dx = -1; dx <= 1; dx++) for (var y = 0; y < 48; y++) for (var x = 0; x < 48; x++)
        {
            var sx = (int)Math.Round((image.Width / scale - 48) / 2 * scale + (x + .5 + dx) * scale);
            var sy = (int)Math.Round((image.Height / scale - 48 + dy) * scale + (y + .5) * scale);
            offsets[((dy + 6) * 3 + dx + 1) * 2304 + y * 48 + x] = sx < 0 || sy < 0 || sx >= image.Width || sy >= image.Height || (sx < image.Width * .34 && sy < image.Height * .18) ? -1 : (sy * image.Width + sx) * 4;
        }
        var bytes = image.Bytes;
        var ranked = _native.Select(template =>
        {
            double best = 0; int bestY = 0, bestX = 0;
            for (var dy = -6; dy <= 10; dy++) for (var dx = -1; dx <= 1; dx++)
            {
                int total = 0, count = 0; var pose = ((dy + 6) * 3 + dx + 1) * 2304;
                for (var pointIndex = 0; pointIndex < template.Points.Length; pointIndex += 3)
                {
                    var point = template.Points[pointIndex];
                    var offset = offsets[pose + point.Offset];
                    if (offset < 0) continue;
                    total += Agreement(point, bytes, offset, grayscale); count++;
                }
                var score = count == 0 ? 0 : total / (count * 7650d);
                if (count >= template.Points.Length * .15 && score > best) { best = score; bestY = dy + 6; bestX = dx + 1; }
            }
            return (Template: template, Score: best, Dy: bestY, Dx: bestX);
        }).OrderByDescending(entry => entry.Score).ToArray();
        // Sprite animation can move the art by a fraction of one native pixel. Refine only
        // shortlisted identities, retaining the full catalog in the first pass.
        for (var index = 0; index < Math.Min(24, ranked.Length); index++)
        {
            var entry = ranked[index]; double refined = 0; var radius = (int)Math.Ceiling(scale / 2);
            for (var oy = -radius; oy <= radius; oy++) for (var ox = -radius; ox <= radius; ox++)
            {
                int total = 0, count = 0;
                foreach (var point in entry.Template.Points)
                {
                    var original = offsets[(entry.Dy * 3 + entry.Dx) * 2304 + point.Offset]; if (original < 0) continue;
                    var sx = original / 4 % image.Width + ox; var sy = original / 4 / image.Width + oy;
                    if (sx < 0 || sy < 0 || sx >= image.Width || sy >= image.Height) continue;
                    total += Agreement(point, bytes, (sy * image.Width + sx) * 4, grayscale); count++;
                }
                if (count >= entry.Template.Points.Length * .45) refined = Math.Max(refined, total / (count * 7650d));
            }
            ranked[index] = entry with { Score = refined };
        }
        ranked = ranked.OrderByDescending(entry => entry.Score).ToArray();
        if (ranked.Length == 0) return new(null, SlotOccupancy.Unknown, ShinyState.Unknown, 0, 0, grayscale, []);
        var best = ranked[0];
        var nextSpecies = ranked.FirstOrDefault(entry => entry.Template.Id != best.Template.Id);
        var nextVariant = ranked.FirstOrDefault(entry => entry.Template.Id == best.Template.Id && entry.Template.Variant != best.Template.Variant);
        var margin = best.Score - nextSpecies.Score;
        var variant = nextVariant.Template is not null && best.Score - nextVariant.Score < .025 ? ShinyState.Unknown : best.Template.Variant;
        if (best.Template.Kind == SlotOccupancy.Creature && (nextVariant.Template is null && _shinySpecies.Contains(best.Template.Id) || !_native.Any(template => template.Id == best.Template.Id && template.Variant == ShinyState.Normal)))
            variant = ShinyState.Unknown;
        return new(best.Template.Id, best.Template.Kind, variant, best.Score, margin, grayscale,
            ranked.DistinctBy(entry => entry.Template.Id).Take(4).Select(entry => new RecognitionCandidate(entry.Template.Id, entry.Score)).ToArray());
    }
    private static int Agreement(ForegroundPoint point, byte[] pixels, int offset, bool grayscale)
    {
        int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
        var difference = grayscale ? Math.Abs(point.R + point.G + point.B - r - g - b) : Math.Abs(point.R - r) + Math.Abs(point.G - g) + Math.Abs(point.B - b);
        return (765 - difference) * 7 + (point.Dark == (r < 65 && g < 65 && b < 65) ? 2295 : 0);
    }
    private static (Template Template, double Score)[] Rank(PixelImage image, double aspect, List<Template> templates, bool grayscale, bool portrait)
    {
        return templates.Select(template =>
        {
            double total = 0; int count = 0;
            for (var y = 0; y < 40; y++) for (var x = 0; x < 32; x++)
            {
                var a = template.Pixels.At(x, y); if (a.A < 128) continue;
                var b = image.At(x, y); count++;
                var aDark = a.R < 65 && a.G < 65 && a.B < 65;
                var bDark = b.R < 65 && b.G < 65 && b.B < 65;
                var color = grayscale ? 1 - Math.Abs((a.R + a.G + a.B) / 3d - (b.R + b.G + b.B) / 3d) / 255 :
                    1 - (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 765d;
                total += portrait ? color : .45 * (aDark == bDark ? 1 : 0) + .55 * color;
            }
            var score = count == 0 ? 0 : total / count;
            if (!portrait) score -= Math.Min(.3, Math.Abs(Math.Log(aspect / template.Aspect)) * .15);
            return (Template: template, Score: score);
        }).OrderByDescending(entry => entry.Score).ToArray();
    }
}
