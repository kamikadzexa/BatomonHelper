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

/// <summary>Normalized regions for Batomon's 16:9 shop view.</summary>
public static class ShopLayout
{
    public static readonly NormalizedRect Day = new(.42, .015, .17, .09);
    public static readonly NormalizedRect Hearts = new(.29, .015, .11, .08);
    public static readonly NormalizedRect Progress = new(.59, .015, .12, .08);
    public static readonly NormalizedRect HitPoints = new(.00, .66, .22, .08);
    public static readonly NormalizedRect Rank = new(.15, .74, .30, .065);
    public static readonly NormalizedRect RerollCost = new(.015, .885, .13, .075);
    public static readonly NormalizedRect UpgradeCost = new(.36, .74, .10, .07);
    public static readonly NormalizedRect Lock = new(.57, .74, .13, .07);
    public static readonly NormalizedRect Trainer = new(.005, .11, .17, .55);
    public static readonly NormalizedRect Inventory = new(.69, .82, .12, .17);

    public static readonly NormalizedRect[] Bench =
    [
        new(.25, .18, .105, .14), new(.36, .18, .105, .14), new(.47, .18, .105, .14), new(.58, .18, .105, .14)
    ];

    public static readonly NormalizedRect[] Team =
    [
        new(.31, .41, .105, .14), new(.42, .41, .105, .14), new(.53, .41, .105, .14),
        new(.31, .57, .105, .14), new(.42, .57, .105, .14), new(.53, .57, .105, .14)
    ];

    public static readonly NormalizedRect[] Shop =
    [
        new(.19, .82, .12, .17), new(.315, .82, .12, .17), new(.44, .82, .12, .17),
        new(.565, .82, .12, .17), new(.69, .82, .12, .17)
    ];

    public static Rectangle DetectContentBounds(Bitmap source)
    {
        // Ultrawide fullscreen puts an opaque black bar on both sides of the 16:9 game content.
        var targetWidth = (int)Math.Round(source.Height * 16d / 9d);
        if (source.Width <= targetWidth + 4) return new Rectangle(0, 0, source.Width, source.Height);
        var excess = source.Width - targetWidth;
        var left = excess / 2;
        var candidate = new Rectangle(left, 0, targetWidth, source.Height);
        return IsMostlyBlack(source, new Rectangle(0, 0, left, source.Height)) &&
               IsMostlyBlack(source, new Rectangle(left + targetWidth, 0, source.Width - left - targetWidth, source.Height))
            ? candidate
            : new Rectangle(0, 0, source.Width, source.Height);
    }

    private static bool IsMostlyBlack(Bitmap bitmap, Rectangle region)
    {
        if (region.Width <= 0) return true;
        var samples = 0;
        var black = 0;
        var stepX = Math.Max(1, region.Width / 20);
        var stepY = Math.Max(1, region.Height / 20);
        for (var y = region.Top; y < region.Bottom; y += stepY)
        for (var x = region.Left; x < region.Right; x += stepX)
        {
            var pixel = bitmap.GetPixel(x, y);
            samples++;
            if (pixel.R < 18 && pixel.G < 18 && pixel.B < 18) black++;
        }
        return samples > 0 && black >= samples * .96;
    }
}
