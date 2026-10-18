using System.Text.Json;
using BatomonHelper.Core;
using BatomonHelper.Overlay;

namespace BatomonHelper.Tests;

public sealed class RunCaptureStoreTests
{
    [Fact]
    public async Task Saves_one_complete_file_with_only_distinct_board_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BatomonHelperTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RunCaptureStore(directory, CatalogSnapshot.Empty, DateTimeOffset.UtcNow);
            var initial = new BoardSnapshot { Day = 1, CapturedAt = DateTimeOffset.UtcNow };
            await store.AppendAsync(1, initial, null);
            await store.AppendAsync(2, initial with { CapturedAt = initial.CapturedAt.AddSeconds(1) }, null);
            await store.AppendAsync(3, initial with { Gold = 10 }, null);
            await store.CompleteAsync();

            var run = JsonSerializer.Deserialize<CapturedRun>(await File.ReadAllTextAsync(store.Path), GameStateBridge.JsonOptions);
            Assert.NotNull(run);
            Assert.Equal(1, run.Version);
            Assert.NotNull(run.EndedAt);
            Assert.Equal([1L, 3L], run.Changes.Select(change => change.BridgeSequence));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
