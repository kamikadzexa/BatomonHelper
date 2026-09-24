using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace BatomonHelper.ScreenReader;

/// <summary>Reads only the foreground Batomon shop regions needed for next-shop odds.</summary>
public sealed class WindowsShopScreenReader : IShopScreenReader
{
    private static readonly Rarity[] RibbonOrder = [Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.SuperRare, Rarity.Legendary];
    public async Task<ShopCaptureResult> CaptureAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        try
        {
            using var game = Process.GetProcessesByName("batomon_showdown").FirstOrDefault();
            if (game is null) return Failure("game-not-running", "Start Batomon and open its shop before capturing.");
            var window = game.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return Failure("game-not-visible", "Batomon must be visible and not minimized.");
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcessId);
            if (foregroundProcessId != (uint)game.Id)
            {
                SetForegroundWindow(window);
                await Task.Delay(100, cancellationToken);
                GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcessId);
                if (foregroundProcessId != (uint)game.Id) return Failure("game-not-foreground", "Batomon could not be focused. Focus its shop window and capture again.");
            }
            if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref client.TopLeft) || !ClientToScreen(window, ref client.BottomRight)) return Failure("client-area", "The Batomon client area could not be read.");
            var width = client.BottomRight.X - client.TopLeft.X;
            var height = client.BottomRight.Y - client.TopLeft.Y;
            if (width <= 0 || height <= 0) return Failure("empty-client", "Batomon has no drawable client area.");

            using var original = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(original)) graphics.CopyFromScreen(client.TopLeft.X, client.TopLeft.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            using var source = original.Clone(ShopLayout.DetectContentBounds(original), PixelFormat.Format32bppPArgb);
            return await RecognizeAsync(source, Encode(source), catalog, cancellationToken);
        }
        catch (Exception exception) when (exception is COMException or TypeLoadException or FileNotFoundException)
        {
            return Failure("ocr-unavailable", "Windows English OCR is unavailable. Install the English OCR language feature.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure("capture-failed", $"Could not capture the shop: {exception.Message}");
        }
    }

    /// <summary>Runs the same normalized recognition pipeline against a saved screenshot for regression tests and diagnostics.</summary>
    public async Task<ShopCaptureResult> RecognizeImageAsync(Bitmap image, CatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        using var source = image.Clone(ShopLayout.DetectContentBounds(image), PixelFormat.Format32bppPArgb);
        return await RecognizeAsync(source, Encode(source), catalog, cancellationToken);
    }

    private async Task<ShopCaptureResult> RecognizeAsync(Bitmap source, CaptureFrame frame, CatalogSnapshot catalog, CancellationToken cancellationToken)
    {
        if (!LooksLikeShop(source)) return new(null, frame, [], [new("not-shop", "The captured frame does not match the Batomon shop layout.", true)]);
        var fields = ImmutableArray.CreateBuilder<RecognizedField>();
        var rank = await NumberAsync("ShopRank", source, ShopLayout.ShopRank, ShopRankOdds.MinimumRank, ShopRankOdds.MaximumRank, fields, cancellationToken);
        var rankFieldIndex = fields.Count - 1;
        var gold = await NumberAsync("Gold", source, ShopLayout.Gold, 0, 9_999, fields, cancellationToken);
        var wholeRibbon = await ReadRibbonPercentagesAsync(source, cancellationToken);
        var usesStandardRibbon = ShopRankOdds.TryFindRank(wholeRibbon, out var inferredRank);
        if (usesStandardRibbon && inferredRank != rank)
        {
            rank = inferredRank;
            var original = fields[rankFieldIndex];
            fields[rankFieldIndex] = Field("ShopRank", rank.ToString(), .90, original.Candidates.Append(new RecognitionCandidate($"fixed-layout ribbon:{rank}", .90)));
        }
        var ribbon = ImmutableArray.CreateBuilder<CapturedRarityOdds>();
        foreach (var rarity in RibbonOrder)
        {
            var value = usesStandardRibbon ? (int?)(ShopRankOdds.ForRank(rank).Single(odds => odds.Rarity == rarity).Probability * 100) : wholeRibbon[(int)rarity];
            ribbon.Add(new(rarity, value is >= 0 and <= 100 ? value.Value / 100m : ShopRankOdds.ForRank(rank).Single(odds => odds.Rarity == rarity).Probability));
            fields.Add(Field($"{rarity}Odds", (value ?? (int)(ribbon[^1].Probability * 100)).ToString(), .82, [], value is null ? "Ribbon OCR needs correction." : null));
        }
        var itemUses = await FractionAsync(source, fields, cancellationToken);
        var snapshot = new ShopOddsSnapshot(null, rank, gold, ribbon.ToImmutable(), itemUses.Current, itemUses.Total, null, DateTimeOffset.UtcNow);
        return new(snapshot, frame, fields.ToImmutable(), []);
    }

    private async Task<int> NumberAsync(string name, Bitmap source, NormalizedRect region, int minimum, int maximum, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken, int? fallback = null)
    {
        using var crop = Crop(source, region);
        var text = await ReadTextAsync(crop, cancellationToken);
        var value = ExtractNumber(text, minimum, maximum);
        var valid = value >= minimum && value <= maximum;
        if (!valid && fallback is { } fallbackValue)
        {
            fields.Add(Field(name, fallbackValue.ToString(), .72, text.Select(candidate => new RecognitionCandidate(candidate, .55)).Append(new($"catalog:${fallbackValue}", .72))));
            return fallbackValue;
        }
        fields.Add(Field(name, valid ? value.ToString() : string.Empty, valid ? .72 : 0, text.Select(candidate => new RecognitionCandidate(candidate, .55)), valid ? null : "Numeric OCR needs correction."));
        return valid ? value : 0;
    }

    private static async Task<int?[]> ReadRibbonPercentagesAsync(Bitmap source, CancellationToken cancellationToken)
    {
        using var crop = Crop(source, ShopLayout.Ribbon);
        var candidates = (await ReadTextAsync(crop, cancellationToken)).Concat(await ReadOddsTextAsync(crop, cancellationToken));
        foreach (var candidate in candidates)
        {
            var values = Regex.Matches(NormalizeDigits(candidate), @"\d+").Select(match => int.TryParse(match.Value, out var value) ? value : -1).Where(value => value is >= 0 and <= 100).ToArray();
            if (values.Length >= 5) return values.TakeLast(5).Cast<int?>().ToArray();
        }
        return [null, null, null, null, null];
    }

    private async Task<(int Current, int Total)> FractionAsync(Bitmap source, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken)
    {
        using var crop = Crop(source, ShopLayout.ItemUseCounter);
        var text = await ReadTextAsync(crop, cancellationToken);
        foreach (var candidate in text)
        {
            var match = Regex.Match(NormalizeDigits(candidate), @"(?<current>\d+)\s*/\s*(?<total>\d+)");
            if (match.Success && int.TryParse(match.Groups["current"].Value, out var current) && int.TryParse(match.Groups["total"].Value, out var total))
            {
                fields.Add(Field("ItemUsesRemaining", current.ToString(), .75, [new(candidate, .75)]));
                fields.Add(Field("DailyItemLimit", total.ToString(), .75, [new(candidate, .75)]));
                return (current, total);
            }
        }
        if (HasVisibleItemCounter(crop))
        {
            fields.Add(Field("ItemUsesRemaining", "1", .72, [new("visible-item-slot:1/1", .72)]));
            fields.Add(Field("DailyItemLimit", "1", .72, [new("visible-item-slot:1/1", .72)]));
            return (1, 1);
        }
        fields.Add(Field("ItemUsesRemaining", string.Empty, 0, [], "Item-use counter needs correction."));
        fields.Add(Field("DailyItemLimit", string.Empty, 0, [], "Item-use counter needs correction."));
        return (0, 0);
    }

    private static bool LooksLikeShop(Bitmap source)
    {
        var header = ShopLayout.TrainerHeader.ToPixels(source.Width, source.Height);
        var shop = ShopLayout.ShopProbe.ToPixels(source.Width, source.Height);
        return CountPixels(source, header, color => color.R > 190 && color.G > 140 && color.B < 80) > 4 && CountPixels(source, shop, color => color.R > 150 || color.G > 150 || color.B > 150) > 20;
    }
    private static bool HasVisibleItemCounter(Bitmap source)
    {
        var lightPixels = 0;
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            if (minimum > 150 && maximum - minimum < 42) lightPixels++;
        }
        return lightPixels >= Math.Max(10, source.Width * source.Height / 90);
    }

    private static int CountPixels(Bitmap image, Rectangle region, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = region.Top; y < region.Bottom; y += Math.Max(1, region.Height / 12)) for (var x = region.Left; x < region.Right; x += Math.Max(1, region.Width / 12)) if (predicate(image.GetPixel(x, y))) count++;
        return count;
    }

    private static int ExtractNumber(IEnumerable<string> candidates, int minimum, int maximum)
    {
        foreach (var candidate in candidates) foreach (Match match in Regex.Matches(NormalizeDigits(candidate), @"\d+")) if (int.TryParse(match.Value, out var value) && value >= minimum && value <= maximum) return value;
        return -1;
    }
    private static string NormalizeDigits(string value) => value.Replace('O', '0').Replace('o', '0').Replace('I', '1').Replace('l', '1');
    private static RecognizedField Field(string name, string value, double confidence, IEnumerable<RecognitionCandidate> candidates, string? diagnostic = null) => new(name, value, confidence, confidence >= .65 && diagnostic is null, candidates.DistinctBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray(), diagnostic);

    private static async Task<IReadOnlyList<string>> ReadTextAsync(Bitmap source, CancellationToken cancellationToken)
    {
        using var enlarged = Enlarge(source);
        using var binary = Binary(source);
        using var enlargedBinary = Enlarge(binary);
        using var lightMask = LightTextMask(source);
        using var enlargedLightMask = Enlarge(lightMask, 6);
        using var inkMask = InkMask(source);
        using var enlargedInkMask = Enlarge(inkMask, 6);
        return (await ReadVariantAsync(enlarged, cancellationToken))
            .Concat(await ReadVariantAsync(enlargedBinary, cancellationToken))
            .Concat(await ReadVariantAsync(enlargedLightMask, cancellationToken))
            .Concat(await ReadVariantAsync(enlargedInkMask, cancellationToken))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static async Task<IReadOnlyList<string>> ReadOddsTextAsync(Bitmap source, CancellationToken cancellationToken)
    {
        using var enlarged = Enlarge(source, 6);
        using var binary = Binary(source);
        using var enlargedBinary = Enlarge(binary, 8);
        using var lightMask = LightTextMask(source);
        using var enlargedLightMask = Enlarge(lightMask, 8);
        using var inkMask = InkMask(source);
        using var enlargedInkMask = Enlarge(inkMask, 8);
        return (await ReadVariantAsync(enlarged, cancellationToken))
            .Concat(await ReadVariantAsync(enlargedBinary, cancellationToken))
            .Concat(await ReadVariantAsync(enlargedLightMask, cancellationToken))
            .Concat(await ReadVariantAsync(enlargedInkMask, cancellationToken))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    private static Bitmap Enlarge(Bitmap source)
        => Enlarge(source, 3);
    private static Bitmap Enlarge(Bitmap source, int scale)
    {
        var result = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result); graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height)); return result;
    }
    private static Bitmap Binary(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++) { var color = source.GetPixel(x, y); var bright = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 >= 160; result.SetPixel(x, y, bright ? Color.White : Color.Black); }
        return result;
    }
    private static Bitmap InkMask(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            var isInk = maximum < 170 || (maximum - minimum > 55 && maximum > 105);
            result.SetPixel(x, y, isInk ? Color.Black : Color.White);
        }
        return result;
    }
    private static Bitmap LightTextMask(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            var isLightGlyph = minimum > 150 && maximum - minimum < 42;
            result.SetPixel(x, y, isLightGlyph ? Color.Black : Color.White);
        }
        return result;
    }
    private static async Task<IReadOnlyList<string>> ReadVariantAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using var encoded = new MemoryStream(); bitmap.Save(encoded, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(encoded.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); }
        stream.Seek(0); var decoder = await BitmapDecoder.CreateAsync(stream); using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var engine = english is null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
        if (engine is null) throw new InvalidOperationException("No Windows OCR language is installed.");
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken); return result.Lines.Select(line => line.Text).ToArray();
    }
    private static Bitmap Crop(Bitmap source, NormalizedRect region) => source.Clone(region.ToPixels(source.Width, source.Height), PixelFormat.Format32bppPArgb);
    private static CaptureFrame Encode(Bitmap bitmap) { using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return new(stream.ToArray(), bitmap.Width, bitmap.Height); }
    private static ShopCaptureResult Failure(string code, string message) => new(null, null, [], [new(code, message, true)]);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref PointNative point);
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public PointNative TopLeft; public PointNative BottomRight; }
}
