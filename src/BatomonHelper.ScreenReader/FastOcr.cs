using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace BatomonHelper.ScreenReader;

/// <summary>One reusable OCR engine; pixels go directly to SoftwareBitmap without PNG conversion.</summary>
internal sealed class FastOcr
{
    private OcrEngine? _engine;
    public async Task<string> ReadAsync(PixelImage image, CancellationToken cancellationToken, string palette = "light") =>
        (await ReadBatchAsync([(image, palette)], cancellationToken))[0];

    public async Task<string[]> ReadBatchAsync(IReadOnlyList<(PixelImage Image, string Palette)> inputs, CancellationToken cancellationToken)
    {
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        _engine ??= english is null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
        var empty = inputs.Select(_ => string.Empty).ToArray();
        if (_engine is null || inputs.Count == 0) return empty;
        const int padding = 24;
        var scale = Math.Min(3, (int)OcrEngine.MaxImageDimension / Math.Max(inputs.Max(input => input.Image.Width) + padding * 2, inputs.Sum(input => input.Image.Height + padding * 2)));
        if (scale < 1) return empty;
        var width = inputs.Max(input => input.Image.Width * scale) + padding * 2;
        var height = inputs.Sum(input => input.Image.Height * scale + padding * 2);
        var bytes = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var rows = new List<(int Top, int Bottom)>(); int top = 0;
        foreach (var input in inputs)
        {
            var pixels = input.Image.Resize(input.Image.Width * scale, input.Image.Height * scale);
            rows.Add((top + padding, top + padding + pixels.Height));
            for (var y = 0; y < pixels.Height; y++) for (var x = 0; x < pixels.Width; x++)
            {
                var ink = PixelDigits.Ink(pixels.At(x, y), input.Palette);
                var offset = ((top + padding + y) * width + padding + x) * 4;
                bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = ink ? (byte)0 : (byte)255;
            }
            top += pixels.Height + padding * 2;
        }
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        using var writer = new DataWriter(); writer.WriteBytes(bytes); bitmap.CopyFromBuffer(writer.DetachBuffer());
        var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return rows.Select(row => string.Join(' ', result.Lines.SelectMany(line => line.Words)
            .Where(word => word.BoundingRect.Y + word.BoundingRect.Height / 2 >= row.Top && word.BoundingRect.Y + word.BoundingRect.Height / 2 < row.Bottom)
            .OrderBy(word => word.BoundingRect.X).Select(word => word.Text))).ToArray();
    }
}
