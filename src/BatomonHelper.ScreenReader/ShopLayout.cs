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
    public static readonly NormalizedRect TrainerHeader = new(.008, .100, .20, .065);
    public static readonly NormalizedRect TrainerEffect = new(.012, .395, .195, .205);
    public static readonly NormalizedRect ShopRank = new(.197, .745, .055, .05);
    public static readonly NormalizedRect Gold = new(.457, .742, .085, .07);
    public static readonly NormalizedRect Ribbon = new(.195, .735, .250, .075);
    public static readonly NormalizedRect[] RibbonOdds =
    [
        new(.252, .745, .028, .05), new(.292, .745, .028, .05), new(.329, .745, .028, .05),
        new(.368, .745, .028, .05), new(.405, .745, .028, .05)
    ];
    public static readonly NormalizedRect ItemUseCounter = new(.69, .825, .09, .05);
    public static readonly NormalizedRect ShopItemName = new(.69, .935, .10, .045);
    public static readonly NormalizedRect ShopItemPrice = new(.77, .935, .035, .045);
    public static readonly NormalizedRect ShopProbe = new(.19, .82, .12, .17);

    public static Rectangle DetectContentBounds(Bitmap source)
    {
        var targetWidth = (int)Math.Round(source.Height * 16d / 9d);
        if (source.Width <= targetWidth + 4) return new Rectangle(0, 0, source.Width, source.Height);
        var excess = source.Width - targetWidth;
        var left = excess / 2;
        var candidate = new Rectangle(left, 0, targetWidth, source.Height);
        return IsMostlyBlack(source, new Rectangle(0, 0, left, source.Height)) && IsMostlyBlack(source, new Rectangle(left + targetWidth, 0, source.Width - left - targetWidth, source.Height))
            ? candidate : new Rectangle(0, 0, source.Width, source.Height);
    }

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
