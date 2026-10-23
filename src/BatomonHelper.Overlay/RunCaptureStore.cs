using System.Collections.Immutable;
using System.Text.Json;
using BatomonHelper.Core;

namespace BatomonHelper.Overlay;

/// <summary>A self-contained, atomically persisted trace of one player run for offline training and rule validation.</summary>
public sealed record CapturedRun(int Version, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string CatalogVersion, string? GameBuild, ImmutableArray<RunCaptureStep> Changes,
    ImmutableArray<CapturedFight> Fights);

public sealed record RunCaptureStep(long BridgeSequence, DateTimeOffset CapturedAt, BoardSnapshot Board,
    GameCombatState? Combat);

/// <summary>Game-authoritative fight evidence for comparing the simulator with the live game.</summary>
public sealed record CapturedFight(GameBattleState Start, GameBattleOutcome? Outcome,
    ImmutableArray<FightCaptureFrame> Frames);
public sealed record FightCaptureFrame(long BridgeSequence, DateTimeOffset CapturedAt, GameBattleFrame Frame);

public sealed class RunCaptureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(GameStateBridge.JsonOptions) { WriteIndented = true };
    private readonly string _path;
    private readonly List<RunCaptureStep> _changes = [];
    private readonly List<CapturedFight> _fights = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTimeOffset _startedAt;
    private readonly string _catalogVersion;
    private readonly string? _gameBuild;
    private string? _lastSignature;

    public RunCaptureStore(string directory, CatalogSnapshot catalog, DateTimeOffset startedAt)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, $"batomon-run-{startedAt:yyyyMMdd-HHmmssfff}.json");
        _startedAt = startedAt;
        _catalogVersion = catalog.WikiVersion ?? "snapshot";
        _gameBuild = catalog.DeclaredSteamBuild;
    }

    public string Path => _path;

    public async Task AppendAsync(long sequence, BoardSnapshot board, GameCombatState? combat, CancellationToken cancellationToken = default)
    {
        // Timestamps are metadata rather than a game change, so they are excluded from deduplication.
        var signature = JsonSerializer.Serialize(new { Board = board with { CapturedAt = default }, Combat = combat }, GameStateBridge.JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (signature == _lastSignature) return;
            _lastSignature = signature;
            _changes.Add(new(sequence, board.CapturedAt, board, combat));
            await WriteAsync(null, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task AppendFightAsync(long sequence, DateTimeOffset capturedAt, GameBattleState battle,
        CancellationToken cancellationToken = default)
    {
        // The starting teams identify a fight. Outcome and each sampled frame are mutable observations.
        var start = battle with { Outcome = null, Frame = null };
        var startSignature = JsonSerializer.Serialize(start, GameStateBridge.JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = _fights.FindIndex(fight => JsonSerializer.Serialize(fight.Start, GameStateBridge.JsonOptions) == startSignature);
            if (index < 0)
            {
                _fights.Add(new(start, battle.Outcome, []));
                index = _fights.Count - 1;
            }

            var fight = _fights[index];
            var frames = fight.Frames;
            if (battle.Frame is { } frame && (frames.IsDefaultOrEmpty || !Equals(frames[^1].Frame, frame)))
                frames = frames.Add(new(sequence, capturedAt, frame));
            _fights[index] = fight with { Outcome = battle.Outcome ?? fight.Outcome, Frames = frames };
            await WriteAsync(null, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await WriteAsync(DateTimeOffset.UtcNow, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        var run = new CapturedRun(2, _startedAt, endedAt, _catalogVersion, _gameBuild, [.. _changes], [.. _fights]);
        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(run, JsonOptions), cancellationToken);
        File.Move(temporaryPath, _path, true);
    }
}
