using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace BatomonHelper.ScreenReader;

public sealed record ScreenTextLine(string Text, NormalizedRect Bounds);
public sealed record PanelRecognition(ShoppingPanel Panel, ImmutableArray<ObservedInventoryEntry> Inventory,
    ImmutableArray<ObservedChoice> Choices, ImmutableArray<CreatureDetails> Details, string Text,
    string? Outcome, bool Resolved)
{
    public bool HasBlockingAnchor { get; init; }
}

/// <summary>Text anchors classify secondary panels; uncertain quantities and choices remain unresolved.</summary>
public sealed class ShoppingPanelReader
{
    private OcrEngine? _ocr;
    public async Task<ImmutableArray<ScreenTextLine>> ReadLinesAsync(Bitmap image, CancellationToken token = default)
    {
        var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        _ocr ??= english is null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(new Language(english.LanguageTag));
        if (_ocr is null) return [];
        var pixels = PixelImage.FromBitmap(image);
        var factor = Math.Min(1d, (OcrEngine.MaxImageDimension - 1d) / Math.Max(pixels.Width, pixels.Height));
        pixels = pixels.Resize(Math.Max(1, (int)(pixels.Width * factor)), Math.Max(1, (int)(pixels.Height * factor)));
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, pixels.Width, pixels.Height, BitmapAlphaMode.Ignore);
        using var writer = new DataWriter(); writer.WriteBytes(pixels.Bytes); bitmap.CopyFromBuffer(writer.DetachBuffer());
        var result = await _ocr.RecognizeAsync(bitmap).AsTask(token);
        return result.Lines.Where(l => l.Words.Count > 0).Select(l =>
        {
            var left = l.Words.Min(w => w.BoundingRect.Left); var top = l.Words.Min(w => w.BoundingRect.Top);
            var right = l.Words.Max(w => w.BoundingRect.Right); var bottom = l.Words.Max(w => w.BoundingRect.Bottom);
            return new ScreenTextLine(l.Text, new(left / pixels.Width, top / pixels.Height, (right - left) / pixels.Width, (bottom - top) / pixels.Height));
        }).ToImmutableArray();
    }
    public static PanelRecognition Parse(ImmutableArray<ScreenTextLine> lines, CatalogSnapshot catalog, BoardSnapshot? board, SlotAddress? hoveredSlot = null)
    {
        var text = string.Join('\n', lines.Select(l => l.Text));
        var upper = text.ToUpperInvariant();
        var panel = ShoppingPanel.Unknown; string? outcome = null;
        if (Regex.IsMatch(upper, @"\b(VICTORY|DEFEAT|DRAW)\b") && Regex.IsMatch(upper, @"\b(CONFIRM|HIDE)\b"))
        { panel = ShoppingPanel.Result; outcome = Regex.Match(upper, @"\b(VICTORY|DEFEAT|DRAW)\b").Value; }
        else if (Regex.IsMatch(upper, @"\b(RUN OVER|RUN COMPLETE|GAME OVER)\b")) panel = ShoppingPanel.RunEnd;
        else if (Regex.IsMatch(upper, @"\bBACKPACK\b")) panel = ShoppingPanel.Backpack;
        else if (Regex.IsMatch(upper, @"\b(CHOOSE|SELECT|PICK)\b.*\bTRINKET\b") || upper.Contains("TRINKET GIFT")) panel = ShoppingPanel.TrinketReward;
        else if (Regex.IsMatch(upper, @"\b(SELECT|CHOOSE|PICK)\b.*\b(MONSTER|TARGET|BATOMON)\b")) panel = ShoppingPanel.TargetSelection;
        else if (Regex.IsMatch(upper, @"\b(EVENT|SECOND CHANCE|TRAVELING MERCHANT)\b") && Regex.IsMatch(upper, @"\b(CHOOSE|SELECT|ACCEPT|DECLINE)\b")) panel = ShoppingPanel.Event;
        else if (upper.Contains("SUDDEN DEATH") || upper.Contains("PLAYBACK") || upper.Contains("MATCHMAKING")) panel = ShoppingPanel.Battle;
        var inventory = ImmutableArray.CreateBuilder<ObservedInventoryEntry>();
        var choices = ImmutableArray.CreateBuilder<ObservedChoice>();
        var details = ImmutableArray.CreateBuilder<CreatureDetails>();
        var entries = catalog.Items.Select(i => (i.Id, i.Name, Trinket: false, Effect: i.RuleText))
            .Concat(catalog.Trinkets.Select(t => (t.Id, t.Name, Trinket: true, Effect: t.RuleText))).ToArray();
        if (panel is ShoppingPanel.Backpack or ShoppingPanel.TrinketReward)
        {
            foreach (var line in lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
            {
                var matches = entries.Where(e => ContainsName(line.Text, e.Name)).ToArray();
                if (matches.Length != 1) continue;
                var entry = matches[0];
                if (panel == ShoppingPanel.Backpack)
                {
                    var quantity = Regex.Match(line.Text, @"(?:[x×]\s*(\d+)|(\d+)\s*[x×])", RegexOptions.IgnoreCase);
                    int? count = quantity.Success ? int.Parse(quantity.Groups[1].Success ? quantity.Groups[1].Value : quantity.Groups[2].Value) : null;
                    inventory.Add(new(inventory.Count, entry.Id, count, entry.Trinket, count is > 0));
                }
                else
                {
                    // Choice IDs may be recognized before geometry/button eligibility is proven. Do not invent enabled=true.
                    choices.Add(new(choices.Count, entry.Name, entry.Id, null, null, entry.Effect, false));
                }
            }
        }
        if (board is not null)
        {
            foreach (var slot in board.Team.Select(s => (Address: new SlotAddress(SlotArea.Team, s.Index), Slot: s))
                .Concat(board.Bench.Select(s => (Address: new SlotAddress(SlotArea.Bench, s.Index), Slot: s))))
            {
                var monster = catalog.Monsters.FirstOrDefault(m => m.Id == slot.Slot.DefinitionId);
                if (monster is null || !lines.Any(l => ContainsName(l.Text, monster.Name))) continue;
                // Actual cursor position identifies the duplicate whose tooltip is visible.
                if (hoveredSlot != slot.Address) continue;
                var sell = Regex.Match(text, @"\bSell\b[^\r\n]*?\$(\d+)", RegexOptions.IgnoreCase);
                var progress = Regex.Match(text, @"\b(?:Merge|Copies)\s*(\d+)\s*/\s*\d+", RegexOptions.IgnoreCase);
                var typeLine = lines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"^Types?\s*:", RegexOptions.IgnoreCase));
                var types = typeLine is null ? ImmutableArray<string>.Empty : typeLine.Text[(typeLine.Text.IndexOf(':') + 1)..]
                    .Split([',', '/', ' '], StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToImmutableArray();
                details.Add(new(slot.Address, types, progress.Success ? int.Parse(progress.Groups[1].Value) : null,
                    sell.Success ? int.Parse(sell.Groups[1].Value) : null, null, typeLine is not null || sell.Success || progress.Success));
            }
        }
        var inventoryComplete = panel == ShoppingPanel.Backpack && Regex.IsMatch(upper, @"\b(NO ITEMS|BACKPACK IS EMPTY|EMPTY BACKPACK)\b");
        return new(panel, inventory.ToImmutable(), choices.ToImmutable(), details.ToImmutable(), text, outcome,
            panel is ShoppingPanel.Result or ShoppingPanel.RunEnd || inventoryComplete)
            { HasBlockingAnchor = Regex.IsMatch(upper,@"\b(SETTINGS|OPTIONS|CHOOSE|SELECT|PICK|REWARD|CONTINUE|PAUSED|RESOLUTION)\b") };
    }
    private static bool ContainsName(string text, string name) => Regex.IsMatch(text, @"(?<![A-Za-z])" + Regex.Escape(name) + @"(?![A-Za-z])", RegexOptions.IgnoreCase);
}
