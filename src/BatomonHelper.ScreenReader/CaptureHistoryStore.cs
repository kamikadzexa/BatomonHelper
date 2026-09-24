using System.Text.Json;
using System.IO;
using System.Collections.Immutable;
using BatomonHelper.Core;

namespace BatomonHelper.ScreenReader;

/// <summary>One reviewed field, retaining both the reader's proposal and the value the player approved.</summary>
public sealed record CaptureFieldReview(string Name, string RecognizedValue, string AcceptedValue, bool WasChanged);

public sealed record CaptureHistoryEntry(
    DateTimeOffset CapturedAt,
    bool Accepted,
    ShopCaptureResult Result,
    ImmutableArray<CaptureFieldReview> Reviews = default);

public sealed class CaptureHistoryStore(string directory, int capacity = 20)
{
    private readonly string _directory = directory;
    private readonly int _capacity = Math.Max(1, capacity);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task SaveAsync(ShopCaptureResult result, bool accepted, ShopOddsSnapshot? acceptedSnapshot = null, IEnumerable<RecognizedField>? acceptedFields = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var id = $"{stamp}-{Guid.NewGuid():N}";
        var finalResult = acceptedSnapshot is null ? result : result with { Snapshot = acceptedSnapshot };
        var reviews = acceptedFields is null
            ? ImmutableArray<CaptureFieldReview>.Empty
            : acceptedFields.Select(field =>
            {
                var recognized = result.Fields.FirstOrDefault(source => string.Equals(source.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                var proposed = recognized?.Value ?? string.Empty;
                return new CaptureFieldReview(field.Name, proposed, field.Value, !string.Equals(proposed, field.Value, StringComparison.Ordinal));
            }).ToImmutableArray();
        var entry = new CaptureHistoryEntry(DateTimeOffset.UtcNow, accepted, finalResult with { Frame = null }, reviews);
        var jsonPath = Path.Combine(_directory, id + ".json");
        var tempJson = jsonPath + ".tmp";
        await File.WriteAllTextAsync(tempJson, JsonSerializer.Serialize(entry, JsonOptions), cancellationToken);
        File.Move(tempJson, jsonPath, true);
        if (result.Frame is not null)
        {
            var imagePath = Path.Combine(_directory, id + ".png");
            var tempImage = imagePath + ".tmp";
            await File.WriteAllBytesAsync(tempImage, result.Frame.Png, cancellationToken);
            File.Move(tempImage, imagePath, true);
        }
        Prune();
    }

    private void Prune()
    {
        var entries = Directory.EnumerateFiles(_directory, "*.json").OrderByDescending(File.GetCreationTimeUtc).Skip(_capacity).ToArray();
        foreach (var jsonPath in entries)
        {
            var imagePath = Path.ChangeExtension(jsonPath, ".png");
            File.Delete(jsonPath);
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
    }
}
