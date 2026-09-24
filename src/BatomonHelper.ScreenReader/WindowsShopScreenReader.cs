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

/// <summary>Reads only a foreground Batomon shop frame. It does not read memory or send game input.</summary>
public sealed class WindowsShopScreenReader : IShopScreenReader
{
    private readonly ReferenceTemplateMatcher _templates = new();

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
                // Clicking the overlay's button necessarily focuses the overlay first. Restoring
                // focus is not game input; it only lets us enforce a foreground-only capture.
                SetForegroundWindow(window);
                await Task.Delay(100, cancellationToken);
                GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcessId);
                if (foregroundProcessId != (uint)game.Id) return Failure("game-not-foreground", "Batomon could not be focused. Focus its shop window and capture again.");
            }
            if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref client.TopLeft) || !ClientToScreen(window, ref client.BottomRight))
                return Failure("client-area", "The Batomon client area could not be read.");
            var width = client.BottomRight.X - client.TopLeft.X;
            var height = client.BottomRight.Y - client.TopLeft.Y;
            if (width <= 0 || height <= 0) return Failure("empty-client", "Batomon has no drawable client area.");

            using var original = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(original)) graphics.CopyFromScreen(client.TopLeft.X, client.TopLeft.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            var content = ShopLayout.DetectContentBounds(original);
            using var source = original.Clone(content, PixelFormat.Format32bppPArgb);
            var frame = Encode(source);
            return await RecognizeAsync(source, frame, catalog, cancellationToken);
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

    private async Task<ShopCaptureResult> RecognizeAsync(Bitmap source, CaptureFrame frame, CatalogSnapshot catalog, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<CaptureDiagnostic>();
        if (!LooksLikeShop(source))
        {
            diagnostics.Add(new("not-shop", "The captured frame does not have the Batomon shop header and card layout.", true));
            return new(null, frame, [], diagnostics.ToImmutable());
        }

        var fields = ImmutableArray.CreateBuilder<RecognizedField>();
        var day = await NumberAsync("Day", source, ShopLayout.Day, 1, 999, fields, cancellationToken);
        var hearts = await NumberAsync("Hearts", source, ShopLayout.Hearts, 0, 99, fields, cancellationToken);
        var progress = await FractionAsync("Progress", source, ShopLayout.Progress, 0, 999, fields, cancellationToken);
        var hitPoints = await NumberAsync("HitPoints", source, ShopLayout.HitPoints, 0, 9_999, fields, cancellationToken);
        var rank = await NumberAsync("Rank", source, ShopLayout.Rank, 1, 99, fields, cancellationToken);
        var reroll = await NumberAsync("RerollCost", source, ShopLayout.RerollCost, 0, 999, fields, cancellationToken);
        var upgrade = await NumberAsync("UpgradeCost", source, ShopLayout.UpgradeCost, 0, 9_999, fields, cancellationToken);
        var lockText = await ReadTextAsync(Crop(source, ShopLayout.Lock), cancellationToken);
        var locked = lockText.Any(text => text.Contains("locked", StringComparison.OrdinalIgnoreCase));
        fields.Add(Field("IsLocked", locked ? "true" : "false", locked ? .85 : .55, lockText.Select(text => new RecognitionCandidate(text, .65)), locked ? null : "Lock button was not positively recognized."));

        var trainerText = await ReadTextAsync(Crop(source, ShopLayout.Trainer), cancellationToken);
        var trainer = trainerText.FirstOrDefault(text => text.Length >= 3);
        fields.Add(Field("Trainer", trainer ?? string.Empty, trainer is null ? 0 : .55, trainerText.Select(text => new RecognitionCandidate(text, .55)), trainer is null ? "Trainer text needs confirmation." : null));

        var team = await ReadUnitsAsync("Team", source, ShopLayout.Team, catalog, fields, cancellationToken);
        var bench = await ReadUnitsAsync("Bench", source, ShopLayout.Bench, catalog, fields, cancellationToken);
        var shop = await ReadShopAsync(source, catalog, fields, cancellationToken);
        var inventory = await ReadInventoryAsync(source, fields, cancellationToken);
        AddReviewOnlyFields(fields);

        var state = new ShopState(
            new ShopHud(day, hearts, progress.Current, progress.Target, hitPoints, rank, 0, 0, reroll, upgrade, locked),
            trainer, "starter", team, bench, shop, inventory, 0, new RunEffects(), DateTimeOffset.UtcNow);
        return new(state, frame, fields.ToImmutable(), diagnostics.ToImmutable());
    }

    private async Task<ImmutableArray<UnitState>> ReadUnitsAsync(string prefix, Bitmap source, IReadOnlyList<NormalizedRect> regions, CatalogSnapshot catalog, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken)
    {
        var units = ImmutableArray.CreateBuilder<UnitState>();
        for (var index = 0; index < regions.Count; index++)
        {
            using var crop = Crop(source, regions[index]);
            if (IsEmptySlot(crop)) { fields.Add(Field($"{prefix}{index + 1}", string.Empty, .98, [])); continue; }
            var template = _templates.Match(crop, "monster");
            var text = await ReadTextAsync(crop, cancellationToken);
            var match = ResolveMonster(text, catalog) ?? template;
            fields.Add(Field($"{prefix}{index + 1}", match?.Value ?? string.Empty, match?.Confidence ?? 0d,
                text.Select(value => new RecognitionCandidate(value, .5)).AppendIfNotNull(template), "Occupied slot requires a fixture match or correction."));
            fields.Add(Field($"{prefix}{index + 1}Level", "1", 0, [], "Confirm the unit level; level 3+ units affect pool eligibility."));
            // Keep an unresolved occupied slot in the state so validation cannot silently
            // exclude a level-three unit from odds. The review dialog must resolve it.
            units.Add(new(match?.Value ?? $"unresolved-{prefix.ToLowerInvariant()}-{index + 1}", 1, false, index, [], ImmutableDictionary<string, int>.Empty));
        }
        return units.ToImmutable();
    }

    private async Task<ImmutableArray<ShopSlotState>> ReadShopAsync(Bitmap source, CatalogSnapshot catalog, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken)
    {
        var slots = ImmutableArray.CreateBuilder<ShopSlotState>();
        for (var index = 0; index < ShopLayout.Shop.Length; index++)
        {
            using var crop = Crop(source, ShopLayout.Shop[index]);
            var text = await ReadTextAsync(crop, cancellationToken);
            var monster = ResolveMonster(text, catalog) ?? _templates.Match(crop, "monster");
            var item = _templates.Match(crop, "item");
            var kind = item is not null || text.Any(value => value.Contains("voucher", StringComparison.OrdinalIgnoreCase) || value.Contains("item", StringComparison.OrdinalIgnoreCase))
                ? ShopSlotKind.Item : monster is null ? ShopSlotKind.Empty : ShopSlotKind.Batomon;
            var price = ExtractNumber(text, 0, 9_999);
            fields.Add(Field($"Shop{index + 1}", monster?.Value ?? item?.Value ?? string.Empty, Math.Max(monster?.Confidence ?? 0, item?.Confidence ?? 0),
                text.Select(value => new RecognitionCandidate(value, .5)).AppendIfNotNull(monster).AppendIfNotNull(item), kind == ShopSlotKind.Empty ? "Offer needs correction." : null));
            fields.Add(Field($"Shop{index + 1}Price", price.ToString(), price > 0 ? .65 : 0d, []));
            slots.Add(new(kind, monster?.Value ?? item?.Value, null, price, false));
        }
        return slots.ToImmutable();
    }

    private async Task<ImmutableArray<string>> ReadInventoryAsync(Bitmap source, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken)
    {
        using var crop = Crop(source, ShopLayout.Inventory);
        var text = await ReadTextAsync(crop, cancellationToken);
        var item = text.FirstOrDefault(value => value.Length >= 3 && !value.Contains("item", StringComparison.OrdinalIgnoreCase));
        fields.Add(Field("Inventory", item ?? string.Empty, item is null ? 0 : .55, text.Select(value => new RecognitionCandidate(value, .55)), item is null ? "Inventory needs confirmation." : null));
        return item is null ? [] : [item];
    }

    private static void AddReviewOnlyFields(ImmutableArray<RecognizedField>.Builder fields)
    {
        fields.Add(Field("ActiveSet", "starter", 0, [], "Confirm the active set for this run."));
        fields.Add(Field("ItemUsesRemaining", "0", 0, [], "Confirm remaining item uses if an inventory item affects shop slots."));
        fields.Add(Field("ResearchNotes", "0", 0, [], "Confirm if Research Notes is active."));
        fields.Add(Field("VipPass", "false", 0, [], "Confirm if VIP Pass is active."));
        fields.Add(Field("MysticIncenseQueued", "false", 0, [], "Confirm if Mystic Incense is queued."));
        fields.Add(Field("ForceAllMythical", "false", 0, [], "Confirm a forced all-Mythical shop."));
        fields.Add(Field("TicketRarity", "", 0, [], "Enter the forced ticket rarity when applicable."));
        fields.Add(Field("RainbowPearl", "false", 0, [], "Confirm if Rainbow Pearl is active."));
    }

    private static bool LooksLikeShop(Bitmap source)
    {
        var top = ShopLayout.Day.ToPixels(source.Width, source.Height);
        var shop = ShopLayout.Shop[0].ToPixels(source.Width, source.Height);
        return ColorCount(source, top, color => color.R > 210 && color.G > 210 && color.B > 210) > 4 &&
               ColorCount(source, shop, color => color.R > 150 || color.G > 150 || color.B > 150) > 20;
    }

    private static int ColorCount(Bitmap image, Rectangle region, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = region.Top; y < region.Bottom; y += Math.Max(1, region.Height / 12))
        for (var x = region.Left; x < region.Right; x += Math.Max(1, region.Width / 12))
            if (predicate(image.GetPixel(x, y))) count++;
        return count;
    }

    private static bool IsEmptySlot(Bitmap image)
    {
        var light = 0;
        var samples = 0;
        for (var y = 4; y < image.Height - 4; y += Math.Max(1, image.Height / 14))
        for (var x = 4; x < image.Width - 4; x += Math.Max(1, image.Width / 14))
        {
            var pixel = image.GetPixel(x, y);
            samples++;
            if (pixel.R > 215 && pixel.G > 215 && pixel.B > 215) light++;
        }
        return samples > 0 && light >= samples * .72;
    }

    private async Task<int> NumberAsync(string name, Bitmap source, NormalizedRect region, int minimum, int maximum, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken)
    {
        using var crop = Crop(source, region);
        var text = await ReadTextAsync(crop, cancellationToken);
        var template = _templates.Match(crop, "number");
        var templatedValue = template is not null && int.TryParse(template.Value, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : minimum - 1;
        var value = templatedValue >= minimum ? templatedValue : ExtractNumber(text, minimum, maximum);
        fields.Add(Field(name, value.ToString(), templatedValue >= minimum ? template!.Confidence : value >= minimum && value <= maximum ? .70 : 0d,
            text.Select(candidate => new RecognitionCandidate(candidate, .55)).AppendIfNotNull(template), value < minimum ? "Numeric OCR needs correction." : null));
        return value < minimum ? minimum : value;
    }

    private async Task<(int Current, int Target)> FractionAsync(string name, Bitmap source, NormalizedRect region, int minimum, int maximum, ImmutableArray<RecognizedField>.Builder fields, CancellationToken cancellationToken)
    {
        using var crop = Crop(source, region);
        var text = await ReadTextAsync(crop, cancellationToken);
        var template = _templates.Match(crop, "fraction");
        var candidates = text.Concat(template is null ? [] : [template.Value]);
        foreach (var candidate in candidates)
        {
            var match = Regex.Match(NormalizeDigits(candidate), @"(?<current>\d+)\s*/\s*(?<target>\d+)");
            if (match.Success && int.TryParse(match.Groups["current"].Value, out var current) && int.TryParse(match.Groups["target"].Value, out var target) && current >= minimum && target <= maximum)
            {
                var confidence = template?.Value == candidate ? template.Confidence : .72;
                fields.Add(Field("ProgressCurrent", current.ToString(), confidence, [new(candidate, confidence)]));
                fields.Add(Field("ProgressTarget", target.ToString(), confidence, [new(candidate, confidence)]));
                return (current, target);
            }
        }
        fields.Add(Field("ProgressCurrent", "0", 0, [], "Progress OCR needs correction."));
        fields.Add(Field("ProgressTarget", "0", 0, [], "Progress OCR needs correction."));
        return (0, 0);
    }

    private static RecognizedField Field(string name, string value, double confidence, IEnumerable<RecognitionCandidate> candidates, string? diagnostic = null) =>
        new(name, value, confidence, confidence >= .65 && diagnostic is null, candidates.DistinctBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray(), diagnostic);

    private static RecognitionCandidate? ResolveMonster(IEnumerable<string> text, CatalogSnapshot catalog)
    {
        var candidates = catalog.Monsters.Select(monster => new RecognitionCandidate(monster.Id, text.Select(value => Similarity(value, monster.Name)).DefaultIfEmpty(0).Max())).OrderByDescending(candidate => candidate.Confidence).ToArray();
        return candidates.FirstOrDefault(candidate => candidate.Confidence >= .78);
    }

    private static double Similarity(string left, string right)
    {
        left = Regex.Replace(left, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase).ToLowerInvariant();
        right = Regex.Replace(right, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase).ToLowerInvariant();
        if (left.Length == 0 || right.Length == 0) return 0;
        var distance = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) distance[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) distance[0, j] = j;
        for (var i = 1; i <= left.Length; i++) for (var j = 1; j <= right.Length; j++) distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
        return 1d - (double)distance[left.Length, right.Length] / Math.Max(left.Length, right.Length);
    }

    private static int ExtractNumber(IEnumerable<string> candidates, int minimum, int maximum)
    {
        foreach (var candidate in candidates)
        foreach (Match match in Regex.Matches(NormalizeDigits(candidate), @"\d+"))
            if (int.TryParse(match.Value, out var value) && value >= minimum && value <= maximum) return value;
        return minimum - 1;
    }

    private static string NormalizeDigits(string value) => value.Replace('O', '0').Replace('o', '0').Replace('I', '1').Replace('l', '1');

    private static async Task<IReadOnlyList<string>> ReadTextAsync(Bitmap source, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (var variant in Variants(source)) lines.AddRange(await ReadVariantAsync(variant, cancellationToken));
        return lines.Where(line => !string.IsNullOrWhiteSpace(line)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<Bitmap> Variants(Bitmap source)
    {
        yield return Enlarge(source);
        using var binary = Binary(source, false);
        yield return Enlarge(binary);
        using var inverted = Binary(source, true);
        yield return Enlarge(inverted);
    }

    private static Bitmap Enlarge(Bitmap source)
    {
        var result = new Bitmap(source.Width * 3, source.Height * 3, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }

    private static Bitmap Binary(Bitmap source, bool inverted)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var bright = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 >= 160;
            result.SetPixel(x, y, bright ^ inverted ? Color.White : Color.Black);
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadVariantAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using (bitmap)
        {
            using var encoded = new MemoryStream();
            bitmap.Save(encoded, ImageFormat.Png);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(encoded.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            var engine = english is null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
            if (engine is null) throw new InvalidOperationException("No Windows OCR language is installed.");
            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            return result.Lines.Select(line => line.Text).ToArray();
        }
    }

    private static Bitmap Crop(Bitmap source, NormalizedRect region) => source.Clone(region.ToPixels(source.Width, source.Height), PixelFormat.Format32bppPArgb);
    private static CaptureFrame Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new(stream.ToArray(), bitmap.Width, bitmap.Height);
    }
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

internal static class RecognitionCandidateExtensions
{
    public static IEnumerable<RecognitionCandidate> AppendIfNotNull(this IEnumerable<RecognitionCandidate> source, RecognitionCandidate? candidate) => candidate is null ? source : source.Append(candidate);
}
