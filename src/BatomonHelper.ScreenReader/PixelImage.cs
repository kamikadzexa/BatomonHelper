using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BatomonHelper.ScreenReader;

internal sealed class PixelImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bytes { get; }
    public PixelImage(int width, int height, byte[] bytes) { Width = width; Height = height; Bytes = bytes; }
    public static PixelImage FromBitmap(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            return new(bitmap.Width, bitmap.Height, bytes);
        }
        finally { bitmap.UnlockBits(data); }
    }
    public Color At(int x, int y)
    { var i = (y * Width + x) * 4; return Color.FromArgb(Bytes[i + 3], Bytes[i + 2], Bytes[i + 1], Bytes[i]); }
    public PixelImage Crop(NormalizedRect rect) => Crop(rect.ToPixels(Width, Height));
    public PixelImage Crop(Rectangle rect)
    {
        var bytes = new byte[rect.Width * rect.Height * 4];
        for (var y = 0; y < rect.Height; y++) Buffer.BlockCopy(Bytes, ((rect.Y + y) * Width + rect.X) * 4, bytes, y * rect.Width * 4, rect.Width * 4);
        return new(rect.Width, rect.Height, bytes);
    }
    public ulong Fingerprint()
    {
        ulong hash = 14695981039346656037;
        foreach (var value in Bytes) { hash ^= value; hash *= 1099511628211; }
        return hash;
    }
    public PixelImage Resize(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var src = (Math.Min(Height - 1, (int)((y + .5) * Height / height)) * Width + Math.Min(Width - 1, (int)((x + .5) * Width / width))) * 4;
            Buffer.BlockCopy(Bytes, src, bytes, (y * width + x) * 4, 4);
        }
        return new(width, height, bytes);
    }
    public Bitmap ToBitmap()
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < Height; y++) Marshal.Copy(Bytes, y * Width * 4, data.Scan0 + y * data.Stride, Width * 4); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    public bool[] Mask(Func<Color, bool> predicate)
    {
        var result = new bool[Width * Height];
        for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++) result[y * Width + x] = predicate(At(x, y));
        return result;
    }
    public static Rectangle Bounds(bool[] mask, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) if (mask[y * width + x])
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    public Rectangle LargestDarkComponent()
    {
        var mask = Mask(c => c.A > 100 && c.R < 65 && c.G < 65 && c.B < 65);
        var best = Rectangle.Empty; var largest = 0; var queue = new Queue<int>();
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            int count = 0, left = Width, top = Height, right = 0, bottom = 0;
            queue.Enqueue(i); mask[i] = false;
            while (queue.TryDequeue(out var p))
            {
                var x = p % Width; var y = p / Width; count++;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                foreach (var next in new[] { x > 0 ? p - 1 : -1, x < Width - 1 ? p + 1 : -1, y > 0 ? p - Width : -1, y < Height - 1 ? p + Width : -1,
                    x > 0 && y > 0 ? p - Width - 1 : -1, x < Width - 1 && y > 0 ? p - Width + 1 : -1,
                    x > 0 && y < Height - 1 ? p + Width - 1 : -1, x < Width - 1 && y < Height - 1 ? p + Width + 1 : -1 })
                    if (next >= 0 && mask[next]) { mask[next] = false; queue.Enqueue(next); }
            }
            if (count > largest) { largest = count; best = Rectangle.FromLTRB(left, top, right + 1, bottom + 1); }
        }
        return best;
    }
}
