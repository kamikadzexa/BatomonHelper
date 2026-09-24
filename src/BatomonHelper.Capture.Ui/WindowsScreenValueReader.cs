using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace BatomonHelper.Capture.Ui;

/// <summary>Captures only the foreground game's client rectangle and never writes an image to disk.</summary>
public sealed class WindowsScreenValueReader : IScreenValueReader
{
    public async Task<ScreenValueReadResult> ReadAsync(int processId, CancellationToken cancellationToken = default)
    {
        var previousDpiContext = IntPtr.Zero;
        var dpiContextChanged = false;
        try
        {
            previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4)); // Per-monitor V2: use physical screen pixels.
            dpiContextChanged = previousDpiContext != IntPtr.Zero;
            using var process = Process.GetProcessById(processId);
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window))
                return Failure("Batomon must be visible and not minimized before reading the screen.");

            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var foregroundProcessId);
            if (foregroundProcessId != (uint)processId)
            {
                SetForegroundWindow(window);
                await Task.Delay(180, cancellationToken);
                GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcessId);
                if (foregroundProcessId != (uint)processId)
                    return Failure("Batomon could not be brought to the front. Make its shop window visible, then try again.");
            }

            if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref client.TopLeft) || !ClientToScreen(window, ref client.BottomRight))
                return Failure("Could not read the Batomon client area.");
            var width = client.BottomRight.X - client.TopLeft.X;
            var height = client.BottomRight.Y - client.TopLeft.Y;
            if (width <= 0 || height <= 0) return Failure("Batomon has no visible client area to read.");

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(client.TopLeft.X, client.TopLeft.Y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            var lines = await ReadHudLinesAsync(bitmap, cancellationToken);
            var values = ScreenValueParser.Parse(lines);
            return new ScreenValueReadResult(values, values.Count == 0 ? "Windows OCR could not read Batomon's pixel-text HUD. Confirm Day, Rank, and Gold manually; the next build will show OCR diagnostics." : null);
        }
        catch (Exception exception) when (exception is COMException or TypeLoadException or FileNotFoundException)
        {
            return Failure("Windows local OCR is unavailable. Install the English OCR language feature, or enter the values manually.");
        }
        catch (Exception exception)
        {
            return Failure($"Could not read the game screen: {exception.Message}");
        }
        finally
        {
            if (dpiContextChanged) SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    private static async Task<IReadOnlyList<(string Text, double Confidence)>> ReadHudLinesAsync(Bitmap source, CancellationToken cancellationToken)
    {
        // Batomon's prominent pixel HUD: top centre Day; bottom Rank and gold panels.
        var regions = new[]
        {
            new Rectangle((int)(source.Width * .42), (int)(source.Height * .02), (int)(source.Width * .17), (int)(source.Height * .11)),
            new Rectangle((int)(source.Width * .19), (int)(source.Height * .74), (int)(source.Width * .16), (int)(source.Height * .10)),
            new Rectangle((int)(source.Width * .45), (int)(source.Height * .74), (int)(source.Width * .11), (int)(source.Height * .11))
        };
        var allLines = new List<(string Text, double Confidence)>();
        foreach (var region in regions)
        {
            using var crop = source.Clone(region, PixelFormat.Format32bppPArgb);
            using var enlarged = EnlargeForPixelText(crop);
            allLines.AddRange(await ReadLinesAsync(enlarged, cancellationToken));
            using var highContrast = HighContrast(crop);
            using var enlargedHighContrast = EnlargeForPixelText(highContrast);
            allLines.AddRange(await ReadLinesAsync(enlargedHighContrast, cancellationToken));
            using var inverted = InvertedHighContrast(crop);
            using var enlargedInverted = EnlargeForPixelText(inverted);
            allLines.AddRange(await ReadLinesAsync(enlargedInverted, cancellationToken));
        }
        return allLines;
    }

    private static Bitmap EnlargeForPixelText(Bitmap source)
    {
        var enlarged = new Bitmap(source.Width * 3, source.Height * 3, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(enlarged);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(0, 0, enlarged.Width, enlarged.Height));
        return enlarged;
    }

    private static Bitmap HighContrast(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var pixel = source.GetPixel(x, y);
            var brightness = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
            result.SetPixel(x, y, brightness >= 170 ? Color.White : Color.Black);
        }
        return result;
    }

    private static Bitmap InvertedHighContrast(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var pixel = source.GetPixel(x, y);
            var brightness = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
            result.SetPixel(x, y, brightness >= 170 ? Color.Black : Color.White);
        }
        return result;
    }

    private static async Task<IReadOnlyList<(string Text, double Confidence)>> ReadLinesAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, ImageFormat.Png);
        var bytes = encoded.ToArray();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var engine = english is null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
        if (engine is null) throw new InvalidOperationException("No Windows OCR language is installed.");
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        return result.Lines.Select(line => (line.Text, 0.80d)).ToArray();
    }

    private static ScreenValueReadResult Failure(string diagnostic) => new([], diagnostic);

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref PointNative point);

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect
    {
        public PointNative TopLeft;
        public PointNative BottomRight;
    }
}
