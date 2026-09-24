using System.Drawing;

namespace BatomonHelper.ScreenReader;

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public Rectangle ToPixels(int width, int height)
    {
        var left = Math.Clamp((int)Math.Round(X * width), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Round(Y * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Round((X + Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Round((Y + Height) * height), top + 1, height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}

/// <summary>Only the stable visual regions needed for a next-shop odds snapshot.</summary>
public static class ShopLayout
{
    public const int MinimumCaptureWidth = 640;
    public const int MinimumCaptureHeight = 360;
    private const double TargetAspectRatio = 16d / 9d;
    private const double AspectRatioTolerance = .025;

    public static readonly NormalizedRect TrainerHeader = new(.008, .100, .20, .065);
    // Keep the rank label wide enough for 10-14, but end before the common-odds glyph.
    public static readonly NormalizedRect ShopRank = new(.197, .745, .060, .05);
    public static readonly NormalizedRect Gold = new(.457, .742, .085, .07);
    public static readonly NormalizedRect Ribbon = new(.195, .735, .250, .075);
    public static readonly NormalizedRect ItemUseCounter = new(.69, .825, .09, .05);
    public static readonly NormalizedRect ShopProbe = new(.19, .82, .12, .17);

    public static bool TryDetectContentBounds(Bitmap source, out Rectangle bounds, out string? failure)
    {
        bounds = Rectangle.Empty;
        failure = null;
        if (source.Width < MinimumCaptureWidth || source.Height < MinimumCaptureHeight)
        {
            failure = $"Capture is too small ({source.Width}×{source.Height}). Use at least {MinimumCaptureWidth}×{MinimumCaptureHeight}.";
            return false;
        }

        var aspect = source.Width / (double)source.Height;
        if (Math.Abs(aspect - TargetAspectRatio) <= AspectRatioTolerance)
        {
            bounds = new Rectangle(0, 0, source.Width, source.Height);
            return true;
        }

        if (aspect > TargetAspectRatio)
        {
            var targetWidth = (int)Math.Round(source.Height * TargetAspectRatio);
            var left = (source.Width - targetWidth) / 2;
            var candidate = new Rectangle(left, 0, targetWidth, source.Height);
            if (IsMostlyBlack(source, new Rectangle(0, 0, left, source.Height)) &&
                IsMostlyBlack(source, new Rectangle(left + targetWidth, 0, source.Width - left - targetWidth, source.Height)))
            {
                bounds = candidate;
                return true;
            }
        }
        else
        {
            var targetHeight = (int)Math.Round(source.Width / TargetAspectRatio);
            var top = (source.Height - targetHeight) / 2;
            var candidate = new Rectangle(0, top, source.Width, targetHeight);
            if (IsMostlyBlack(source, new Rectangle(0, 0, source.Width, top)) &&
                IsMostlyBlack(source, new Rectangle(0, top + targetHeight, source.Width, source.Height - top - targetHeight)))
            {
                bounds = candidate;
                return true;
            }
        }

        failure = $"Capture aspect ratio {aspect:F3} is unsupported. Use the Batomon 16:9 game view without cropping.";
        return false;
    }

    public static Rectangle DetectContentBounds(Bitmap source)
    {
        if (TryDetectContentBounds(source, out var bounds, out _)) return bounds;
        return new Rectangle(0, 0, source.Width, source.Height);
    }

    public static CaptureContentBounds DescribeContentBounds(Size sourceSize, Rectangle bounds)
        => new(sourceSize.Width, sourceSize.Height, bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static bool IsMostlyBlack(Bitmap bitmap, Rectangle region)
    {
        if (region.Width <= 0) return true;
        var samples = 0;
        var black = 0;
        for (var y = region.Top; y < region.Bottom; y += Math.Max(1, region.Height / 20))
        for (var x = region.Left; x < region.Right; x += Math.Max(1, region.Width / 20))
        {
            var pixel = bitmap.GetPixel(x, y);
            samples++;
            if (pixel.R < 18 && pixel.G < 18 && pixel.B < 18) black++;
        }
        return samples > 0 && black >= samples * .96;
    }
}
