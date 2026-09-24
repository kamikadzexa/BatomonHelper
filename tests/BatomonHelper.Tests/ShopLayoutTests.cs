using BatomonHelper.ScreenReader;
using System.Drawing;
using System.Drawing.Imaging;

namespace BatomonHelper.Tests;

public sealed class ShopLayoutTests
{
    [Fact]
    public void Accepts_near_sixteen_by_nine_capture_without_cropping()
    {
        using var image = new Bitmap(1600, 901, PixelFormat.Format32bppPArgb);

        Assert.True(ShopLayout.TryDetectContentBounds(image, out var bounds, out var failure), failure);
        Assert.Equal(new Rectangle(0, 0, 1600, 901), bounds);
    }

    [Fact]
    public void Removes_verified_pillarbox_bars()
    {
        using var image = new Bitmap(1800, 900, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, new Rectangle(100, 0, 1600, 900));
        }

        Assert.True(ShopLayout.TryDetectContentBounds(image, out var bounds, out var failure), failure);
        Assert.Equal(new Rectangle(100, 0, 1600, 900), bounds);
    }

    [Theory]
    [InlineData(639, 360)]
    [InlineData(640, 359)]
    [InlineData(1200, 900)]
    public void Rejects_undersized_or_non_sixteen_by_nine_capture(int width, int height)
    {
        using var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);

        Assert.False(ShopLayout.TryDetectContentBounds(image, out _, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }
}
