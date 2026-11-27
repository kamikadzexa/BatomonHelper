using System.Collections.Immutable;
using System.Text.Json;
using BatomonHelper.Core;

namespace BatomonHelper.Overlay;

/// <summary>A self-contained, atomically persisted trace of one player run for offline training and rule validation.</summary>
public sealed record CapturedRun(int Version, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string CatalogVersion, string? GameBuild, ImmutableArray<RunCaptureStep> Changes,
    ImmutableArray<CapturedFight> Fights, string? CatalogBuild = null, bool Partial = false);

public sealed record RunCaptureStep(long BridgeSequence, DateTimeOffset CapturedAt, BoardSnapshot Board,
    GameCombatState? Combat);

/// <summary>Game-authoritative fight evidence for comparing the simulator with the live game.</summary>
public sealed record CapturedFight(GameBattleState Start, GameBattleOutcome? Outcome,
    ImmutableArray<FightCaptureFrame> Frames, bool TerminalObserved = false);
public sealed record FightCaptureFrame(long BridgeSequence, DateTimeOffset CapturedAt, GameBattleFrame Frame,
    long? ObservationIndex = null);

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
    private readonly string? _catalogBuild;
    private readonly bool _partial;
    private readonly Dictionary<string, int> _fightIndices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastFrameSignatures = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPersisted;
    private string? _lastSignature;

    public RunCaptureStore(string directory, CatalogSnapshot catalog, DateTimeOffset startedAt, string? gameBuild = null, bool partial = false)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, $"batomon-run-{startedAt:yyyyMMdd-HHmmssfff}.json");
        _startedAt = startedAt;
        _catalogVersion = catalog.WikiVersion ?? "snapshot";
        _catalogBuild = catalog.DeclaredSteamBuild;
        _gameBuild = gameBuild;
        _partial = partial;
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
        var start = battle with { Outcome = null, Frame = null, Observations = null };
        var startSignature = start.FightId ?? JsonSerializer.Serialize(start, GameStateBridge.JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_changes.Count > 0 && start.Player.Day < _changes[0].Board.Day) return;
            var newFight = !_fightIndices.TryGetValue(startSignature, out var index);
            if (newFight)
            {
                index = _fights.Count;
                _fightIndices.Add(startSignature, index);
                _fights.Add(new(start, null, []));
            }

            var fight = _fights[index];
            var frames = fight.Frames;
            var terminal = fight.TerminalObserved;
            if (battle.Observations is { } observations)
            {
                var lastIndex = frames.IsEmpty ? 0 : frames[^1].ObservationIndex.GetValueOrDefault();
                var additions = new List<FightCaptureFrame>();
                foreach (var observation in observations.OrderBy(sample => sample.Index))
                {
                    if (observation.Index <= lastIndex) continue;
                    lastIndex = observation.Index;
                    additions.Add(new(sequence, capturedAt, observation.Frame, observation.Index));
                    terminal |= IsTerminal(observation.Frame);
                }
                if (additions.Count > 0) frames = frames.AddRange(additions);
            }
            else if (battle.Frame is { } frame && (frames.IsEmpty || frames[^1].ObservationIndex is null))
            {
                var signature = JsonSerializer.Serialize(frame, GameStateBridge.JsonOptions);
                if (_lastFrameSignatures.GetValueOrDefault(startSignature) != signature)
                {
                    _lastFrameSignatures[startSignature] = signature;
                    frames = frames.Add(new(sequence, capturedAt, frame));
                    terminal |= IsTerminal(frame);
                }
            }
            var outcome = battle.Outcome ?? fight.Outcome;
            var outcomeChanged = outcome != fight.Outcome;
            if (!newFight && frames == fight.Frames && !outcomeChanged) return;
            _fights[index] = fight with { Outcome = outcome, Frames = frames, TerminalObserved = terminal };
            if (newFight || outcomeChanged || terminal && !fight.TerminalObserved ||
                DateTimeOffset.UtcNow - _lastPersisted >= TimeSpan.FromSeconds(2))
                await WriteAsync(null, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static bool IsTerminal(GameBattleFrame frame) => frame.Player.Health <= 0 || frame.Opponent.Health <= 0;

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await WriteAsync(DateTimeOffset.UtcNow, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        // Older installed bridges lack trainer metadata and still emit raw player
        // frame indices. Do not label those recordings as corrected screen order.
        var version = _fights.Any(fight => fight.Start.Player.Combat.Counters is null) ? 2 : 3;
        if (version == 3 && (_partial || _gameBuild is not null || _fights.Any(fight => fight.Start.FightId is not null))) version = 4;
        if (version >= 3 && _fights.Any(fight => !fight.Frames.IsEmpty && fight.Frames[^1].ObservationIndex is not null)) version = 5;
        var run = new CapturedRun(version, _startedAt, endedAt, _catalogVersion, _gameBuild, [.. _changes], [.. _fights], _catalogBuild, _partial);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, run, version == 5 ? GameStateBridge.JsonOptions : JsonOptions, cancellationToken);
        File.Move(temporaryPath, _path, true);
        _lastPersisted = DateTimeOffset.UtcNow;
    }
}
