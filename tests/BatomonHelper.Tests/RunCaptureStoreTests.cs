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
            Assert.Equal(3, run.Version);
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
            Assert.Equal(2, run.Version); // An old bridge's frames retain their legacy schema marker.
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
    [Fact]
    public async Task Records_partial_runs_and_deduplicates_equal_zero_time_frames_without_losing_start_effects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BatomonHelperTests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new RunCaptureStore(directory, CatalogSnapshot.Empty, now, gameBuild: "26773903", partial: true);
            var combat = new GameCombatState([], [], Counters: [], ExtraTrainers: []);
            var side = new GameBattleSide(20, 453800, "fixture", [], combat);
            var frameSide = new GameBattleFrameSide(453800, 453800, 0, 0, 0, 0, []);
            var first = new GameBattleState(side, side, Frame: new(0, frameSide, frameSide), FightId: "10-1");
            await store.AppendAsync(10, new BoardSnapshot { Day = 20, CapturedAt = now }, combat);
            await store.AppendFightAsync(11, now, first);
            await store.AppendFightAsync(12, now, first with { Frame = first.Frame! with { Player = frameSide with { Units = [] } } });
            await store.AppendFightAsync(13, now, first with { Frame = first.Frame! with { Player = frameSide with { Shield = 12 } } });
            await store.AppendFightAsync(14, now, first with { Outcome = new(true),
                Frame = first.Frame! with { Player = frameSide with { Shield = 12 }, Opponent = frameSide with { Health = 0 } } });
            await store.CompleteAsync();

            var capture = JsonSerializer.Deserialize<CapturedRun>(await File.ReadAllTextAsync(store.Path), GameStateBridge.JsonOptions)!;
            Assert.Equal(4, capture.Version);
            Assert.True(capture.Partial);
            Assert.Equal("26773903", capture.GameBuild);
            var fight = Assert.Single(capture.Fights);
            Assert.True(fight.TerminalObserved);
            Assert.Equal([11L, 13L, 14L], fight.Frames.Select(frame => frame.BridgeSequence));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

}
