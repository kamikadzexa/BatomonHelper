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
            Assert.Equal(2, run.Version);
            Assert.NotNull(run.EndedAt);
            Assert.Equal([1L, 3L], run.Changes.Select(change => change.BridgeSequence));
            Assert.Empty(run.Fights);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Saves_battle_start_outcome_and_sampled_game_frames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BatomonHelperTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RunCaptureStore(directory, CatalogSnapshot.Empty, DateTimeOffset.UtcNow);
            var combat = new GameCombatState([], []);
            var side = new GameBattleSide(1, 100, "fixture", [], combat);
            var sample = new GameBattleFrame(0.5,
                new(95, 100, 2, 1, 0, 0, [new(0, true, 1, new double[9], 6, 0, 0)]),
                new(90, 100, 0, 0, 0, 0, []));
            var battle = new GameBattleState(side, side, null, sample);
            await store.AppendFightAsync(10, DateTimeOffset.UtcNow, battle);
            await store.AppendFightAsync(11, DateTimeOffset.UtcNow.AddMilliseconds(500), battle with { Outcome = new(true) });
            await store.CompleteAsync();

            var run = JsonSerializer.Deserialize<CapturedRun>(await File.ReadAllTextAsync(store.Path), GameStateBridge.JsonOptions)!;
            var fight = Assert.Single(run.Fights);
            Assert.Null(fight.Start.Outcome);
            Assert.Null(fight.Start.Frame);
            Assert.True(fight.Outcome!.PlayerWon);
            Assert.Single(fight.Frames);
            Assert.Equal(1, fight.Frames[0].Frame.Player.Units[0].Casts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
