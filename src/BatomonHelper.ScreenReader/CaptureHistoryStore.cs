using System.Text.Json;
using System.IO;

namespace BatomonHelper.ScreenReader;

public sealed record CaptureHistoryEntry(DateTimeOffset CapturedAt, bool Accepted, ShopCaptureResult Result);

public sealed class CaptureHistoryStore(string directory, int capacity = 20)
{
    private readonly string _directory = directory;
    private readonly int _capacity = Math.Max(1, capacity);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task SaveAsync(ShopCaptureResult result, bool accepted, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var id = $"{stamp}-{Guid.NewGuid():N}";
        var entry = new CaptureHistoryEntry(DateTimeOffset.UtcNow, accepted, result with { Frame = null });
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
