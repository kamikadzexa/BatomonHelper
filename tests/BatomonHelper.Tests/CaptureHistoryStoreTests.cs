using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class CaptureHistoryStoreTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 0)]
    [InlineData(3, 50)]
    public void Reads_supplied_V1_history_without_rewriting_it(int day, int gold)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"board-day{day}.v1.json");
        var record = JsonSerializer.Deserialize<CaptureHistoryEntry>(File.ReadAllText(path))!;
        Assert.True(record.Accepted); Assert.Equal(gold, record.Result.Snapshot!.Gold);
        Assert.Null(record.Result.Snapshot.Trainer); Assert.False(record.Reviews.IsDefault);
    }
    [Fact]
    public async Task Version_two_keeps_proposals_separate_and_retains_legacy_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"batomon-history-{Guid.NewGuid():N}");
        try
        {
            var store = new CaptureHistoryStore(directory);
            var legacy = new ShopCaptureResult(new(null, 1, 10, ShopRankOdds.ForRank(1), true, null, DateTimeOffset.UtcNow), null, [], []);
            await store.SaveAsync(legacy, true);
            var oldPath = Assert.Single(Directory.EnumerateFiles(directory, "*.json")); var oldBytes = await File.ReadAllBytesAsync(oldPath);
            var proposal = new BoardCaptureResult(new BoardSnapshot { Gold = 10 }, null, [new("Gold", "10", .7, false, [])], [], new(50, 0, 1));
            await store.SaveBoardAsync(proposal, true, proposal.Snapshot! with { Gold = 11 }, [new("Gold", "11", 1, true, [])]);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath));
            var recordPath = Directory.EnumerateFiles(directory, "*.json").Single(path => path != oldPath);
            var record = JsonSerializer.Deserialize<BoardCaptureHistoryEntry>(await File.ReadAllTextAsync(recordPath))!;
            Assert.Equal(2, record.Version); Assert.Equal(10, record.Result.Snapshot!.Gold); Assert.Equal(11, record.AcceptedSnapshot!.Gold);
            Assert.True(Assert.Single(record.Reviews).WasChanged);
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => store.SaveBoardAsync(proposal, false)));
            Assert.Equal(20, Directory.EnumerateFiles(directory, "*.json").Count());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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
