using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class CaptureHistoryStoreTests
{
    [Fact]
    public async Task Persists_the_reader_guess_and_the_reviewed_value()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"batomon-history-{Guid.NewGuid():N}");
        try
        {
            var snapshot = new ShopOddsSnapshot(null, 1, 10, ShopRankOdds.ForRank(1), true, null, DateTimeOffset.UtcNow);
            var result = new ShopCaptureResult(snapshot, null,
                [new("Gold", "10", .7, true, [], null)], []);
            var corrected = snapshot with { Gold = 11 };

            await new CaptureHistoryStore(directory).SaveAsync(result, true, corrected, [new("Gold", "11", .7, true, [], null)]);

            var json = await File.ReadAllTextAsync(Assert.Single(Directory.EnumerateFiles(directory, "*.json")));
            Assert.Contains("\"RecognizedValue\": \"10\"", json);
            Assert.Contains("\"AcceptedValue\": \"11\"", json);
            Assert.Contains("\"WasChanged\": true", json);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
