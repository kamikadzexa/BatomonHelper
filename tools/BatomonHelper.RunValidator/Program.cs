using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using BatomonHelper.Core;
using BatomonHelper.Simulation;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: RunValidator <combat-rules.json> <run.json> [run.json ...] [--benchmark]");
    return 2;
}
var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(args[0]))!;
var reports = new List<object>();
var failures = 0;
var benchmark = args.Contains("--benchmark");
foreach (var path in args.Skip(1).Where(argument => argument != "--benchmark"))
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var run = document.RootElement;
    var version = run.GetProperty("version").GetInt32();
    var changes = run.GetProperty("changes").EnumerateArray().Select(change => (
        Board: change.GetProperty("board").Deserialize<BoardSnapshot>(GameStateBridge.JsonOptions)!,
        Combat: change.GetProperty("combat").Deserialize<GameCombatState>(GameStateBridge.JsonOptions)))
        .Where(change => change.Combat is not null).ToArray();
    foreach (var (board, combat) in changes)
        LiveBoardImport.Create(board with { Team = Normalize(board.Team), Bench = Normalize(board.Bench) }, combat!, rules);

    var fights = new List<object>();
    var benchmarkInputs = new List<(FrozenBoard Board, Unit?[] Bench, double Target)>();
    if (benchmark && changes.LastOrDefault() is ({ } lateBoard, { } lateCombat))
    {
        var late = LiveBoardImport.Create(lateBoard with { Team = Normalize(lateBoard.Team), Bench = Normalize(lateBoard.Bench) },
            lateCombat, rules);
        benchmarkInputs.Add((late.Board, late.Bench ?? new Unit?[4], (double)lateBoard.TeamHp!.Value));
    }
    foreach (var fight in run.GetProperty("fights").EnumerateArray())
    {
        var start = fight.GetProperty("start").Deserialize<GameBattleState>(GameStateBridge.JsonOptions)!;
        var player = LiveBoardImport.CreateBattleSide(start.Player with { Team = Normalize(start.Player.Team) }, rules);
        var enemy = LiveBoardImport.CreateBattleSide(start.Opponent with { Team = Normalize(start.Opponent.Team) }, rules, mirrored: true);
        var resolver = new Combat(rules);
        var observed = fight.GetProperty("frames").EnumerateArray().Select(entry => new FrameObservation(
            entry.TryGetProperty("observationIndex", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt64() : null,
            entry.GetProperty("frame").Deserialize<GameBattleFrame>(GameStateBridge.JsonOptions)!)).ToArray();
        if (version == 2)
            observed = observed.Select(observation => observation with
            {
                Frame = observation.Frame with
                {
                    Player = observation.Frame.Player with
                    {
                        Units = observation.Frame.Player.Units.Select(unit => unit with { Slot = 5 - unit.Slot }).ToArray()
                    }
                }
            }).ToArray();

        var timeline = new List<GameBattleFrame>();
        var hasProgressSamples = observed.Any(sample => sample.Frame.Player.Units.Concat(sample.Frame.Opponent.Units)
            .Any(unit => unit.CooldownProgressSeconds is not null));
        // Cooldown progress advances on ticks without a cast or status change.
        var simulated = resolver.ResolveBattle(player.Board, enemy.Board, 69, audit: hasProgressSamples ? null : timeline.Add);
        var reference = resolver.ResolveBattle(player.Board, enemy.Board, 69, reference: true,
            audit: hasProgressSamples ? timeline.Add : null);
        var outcome = fight.GetProperty("outcome").Deserialize<GameBattleOutcome>(GameStateBridge.JsonOptions);
        var matches = outcome is null ? (bool?)null : outcome.PlayerWon == simulated.PlayerWon;
        var agrees = simulated.PlayerWon == reference.PlayerWon && Math.Abs(simulated.Seconds - reference.Seconds) < .001
            && Math.Abs(simulated.PlayerHp - reference.PlayerHp) < .001 && Math.Abs(simulated.OpponentHp - reference.OpponentHp) < .001;
        if (matches == false || !agrees) failures++;
        var (firstPlayback, coverage) = InspectCoverage(observed);
        var distinct = firstPlayback.DistinctBy(frame => JsonSerializer.Serialize(frame, GameStateBridge.JsonOptions)).ToArray();
        // The latest observed state at 0s is the closest sample to completed battle start.
        var samples = firstPlayback.GroupBy(frame => frame.ElapsedSeconds).Select(group => group.Last()).ToArray();
        var aligned = Align(samples, timeline, simulated.Seconds).ToArray();
        var distinctOpeningStates = distinct.Count(frame => frame.ElapsedSeconds <= 1e-7);
        var preCastSamples = samples.TakeWhile(frame => frame.Player.Units.All(unit => unit.Casts == 0) &&
            frame.Opponent.Units.All(unit => unit.Casts == 0));
        var opening = preCastSamples.FirstOrDefault(frame => frame.ElapsedSeconds > 1e-7 || distinctOpeningStates > 1);
        var terminal = firstPlayback.FirstOrDefault(IsTerminal);
        var latestAlive = firstPlayback.LastOrDefault(frame => !IsTerminal(frame));
        fights.Add(new
        {
            day = start.Player.Day, gameWon = outcome?.PlayerWon, simulatedWon = simulated.PlayerWon,
            outcomeMatches = matches, referenceAgrees = agrees, simulatedSeconds = simulated.Seconds,
            simultaneousDefeat = simulated.BothDefeated,
            simulatedEndingHp = new { player = simulated.PlayerHp, opponent = simulated.OpponentHp },
            startingHp = new { player = start.Player.TeamHp, opponent = start.Opponent.TeamHp },
            samples = observed.Length, distinctStates = distinct.Length, comparedSamples = aligned.Length,
            coverage,
            terminalObserved = terminal is not null, recordedTerminalSeconds = terminal?.ElapsedSeconds,
            lastBothAliveSeconds = latestAlive?.ElapsedSeconds,
            simulatedEndedBeforeRecordedSurvival = latestAlive is not null && simulated.Seconds + .001 < latestAlive.ElapsedSeconds,
            trainerProgressRecorded = start.Player.Combat.Counters is not null && start.Player.Combat.ExtraTrainers is not null,
            battleStart = opening is null ? null : new
            {
                sampleSeconds = opening.ElapsedSeconds,
                distinctOpeningStates,
                player = Compare([(opening.ElapsedSeconds, opening.Player, timeline[0].Player)]),
                opponent = Compare([(opening.ElapsedSeconds, opening.Opponent, timeline[0].Opponent)])
            },
            timeline = new
            {
                player = Compare(aligned.Select(x => (x.Game.ElapsedSeconds, x.Game.Player, x.Simulated.Player))),
                opponent = Compare(aligned.Select(x => (x.Game.ElapsedSeconds, x.Game.Opponent, x.Simulated.Opponent)))
            }
        });
        if (benchmark && start.Player.Day == 20)
            benchmarkInputs.Add((player.Board, new Unit?[4], start.Opponent.TeamHp));
    }
    reports.Add(new
    {
        file = Path.GetFileName(path), version,
        gameBuild = version >= 4 ? run.GetProperty("gameBuild").GetString() : null,
        catalogBuild = run.GetProperty(version >= 4 ? "catalogBuild" : "gameBuild").GetString(),
        analyzedBuild = rules.Build, importedShopBoards = changes.Length, lastShopDay = changes.LastOrDefault().Board?.Day,
        fights,
        timings = benchmarkInputs.Select(input => Enum.GetValues<PlacementObjective>().Select(objective =>
        {
            var timer = Stopwatch.StartNew();
            _ = new PositionAdvisor(rules).FindBetterPosition(input.Board, input.Bench, input.Target, objective: objective);
            return new { input.Board.Day, Objective = objective.ToString(), milliseconds = timer.Elapsed.TotalMilliseconds };
        }).ToArray()).ToArray()
    });
}
Console.WriteLine(JsonSerializer.Serialize(new { seed = 69, analyzerVersion = BoardAnalyzer.Version, rulesHash = rules.Hash, reports, failures,
    limitations = new[] { "The game RNG seed was not recorded. Timeline differences are diagnostics.",
        "Timeline comparison uses the first observed playback and the latest state at each timestamp. Cooldown progress uses the fixed-tick reference timeline.",
        "Intervals without a terminal sample give a lower bound on fight duration. Missing cooldown fields indicate unavailable game telemetry.",
        "Per-unit damage compares direct cast output. Game totals may also attribute status damage.",
        "In versions 2 and 3, the field named gameBuild records the helper catalog build. Version 2 player frame slots are reversed." }
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

ImmutableArray<BoardSlot> Normalize(ImmutableArray<BoardSlot> slots) => slots.IsDefault ? [] : slots.Select(slot => slot with
{
    DefinitionId = slot.DefinitionId is null ? null : GameCatalogIds.Resolve(slot.DefinitionId, rules.Species.Select(species => species.Id))
}).ToImmutableArray();

static bool IsTerminal(GameBattleFrame frame) => frame.Player.Health <= 0 || frame.Opponent.Health <= 0;

static (GameBattleFrame[] FirstPlayback, object Coverage) InspectCoverage(FrameObservation[] observed)
{
    var firstPlaybackCount = observed.Length;
    var playbackResets = 0;
    var gapsOverHalfSecond = 0;
    var longestGap = 0d;
    var lastIndex = 0L;
    var missingObservations = 0L;
    var nonIncreasingIndices = 0;
    for (var i = 0; i < observed.Length; i++)
    {
        if (observed[i].Index is { } index)
        {
            if (index <= lastIndex) nonIncreasingIndices++;
            else
            {
                missingObservations += index - lastIndex - 1;
                lastIndex = index;
            }
        }
        if (i == 0) continue;
        var gap = observed[i].Frame.ElapsedSeconds - observed[i - 1].Frame.ElapsedSeconds;
        if (gap < -1e-7)
        {
            firstPlaybackCount = Math.Min(firstPlaybackCount, i);
            playbackResets++;
        }
        if (gap > .5000001) gapsOverHalfSecond++;
        longestGap = Math.Max(longestGap, gap);
    }
    var frames = observed.Take(firstPlaybackCount).Select(sample => sample.Frame).ToArray();
    var zeroSecondSamples = frames.Count(frame => frame.ElapsedSeconds <= 1e-7);
    return (frames, new
    {
        firstSampleSeconds = frames.FirstOrDefault()?.ElapsedSeconds,
        zeroSecondSamples,
        missingStartSample = zeroSecondSamples == 0,
        missingTerminalSample = !frames.Any(IsTerminal),
        terminalObservedAcrossPlaybacks = observed.Any(sample => IsTerminal(sample.Frame)),
        playbackResets, gapsOverHalfSecond, longestGapSeconds = longestGap,
        missingObservationIndices = lastIndex == 0 ? (long?)null : missingObservations,
        nonIncreasingIndices
    });
}

static IEnumerable<(GameBattleFrame Game, GameBattleFrame Simulated)> Align(GameBattleFrame[] samples,
    List<GameBattleFrame> timeline, double simulatedSeconds)
{
    var index = 0;
    foreach (var frame in samples)
    {
        if (frame.ElapsedSeconds > simulatedSeconds + 1e-7) yield break;
        while (index + 1 < timeline.Count && timeline[index + 1].ElapsedSeconds <= frame.ElapsedSeconds + 1e-7)
            index++;
        yield return (frame, timeline[index]);
    }
}

static object? Compare(IEnumerable<(double Time, GameBattleFrameSide Game, GameBattleFrameSide Simulated)> pairs)
{
    var samples = pairs.ToArray();
    if (samples.Length == 0) return null;
    var units = samples.SelectMany(sample => sample.Game.Units.Join(sample.Simulated.Units, game => game.Slot, sim => sim.Slot,
        (game, sim) => (sample.Time, Game: game, Simulated: sim))).ToArray();
    return new
    {
        maxHpError = samples.Max(sample => Math.Abs(sample.Game.Health - sample.Simulated.Health)),
        maxStartingHpError = samples.Max(sample => Math.Abs(sample.Game.MaximumHealth - sample.Simulated.MaximumHealth)),
        maxShieldError = samples.Max(sample => Math.Abs(sample.Game.Shield - sample.Simulated.Shield)),
        maxBurnError = samples.Max(sample => Math.Abs(sample.Game.Burn - sample.Simulated.Burn)),
        maxPoisonError = samples.Max(sample => Math.Abs(sample.Game.Poison - sample.Simulated.Poison)),
        maxShockError = samples.Max(sample => Math.Abs(sample.Game.Shock - sample.Simulated.Shock)),
        gameOnlySlots = UnmatchedSlots(samples.Select(sample => (sample.Time, sample.Game, sample.Simulated))),
        simulationOnlySlots = UnmatchedSlots(samples.Select(sample => (sample.Time, sample.Simulated, sample.Game))),
        bySlot = units.GroupBy(unit => unit.Game.Slot).OrderBy(group => group.Key).Select(group => new
        {
            slot = group.Key,
            maxCooldownSpeedError = group.Max(unit => Math.Abs(unit.Game.Stats[(int)Stat.Speed] - unit.Simulated.Stats[(int)Stat.Speed])),
            maxCastError = group.Max(unit => Math.Abs(unit.Game.Casts - unit.Simulated.Casts)),
            maxDamageOutputError = group.Max(unit => Math.Abs(unit.Game.DamageDealt - unit.Simulated.DamageDealt)),
            maxHealingOutputError = group.Max(unit => Math.Abs(unit.Game.HealingDone - unit.Simulated.HealingDone)),
            maxShieldOutputError = group.Max(unit => Math.Abs(unit.Game.ShieldGranted - unit.Simulated.ShieldGranted)),
            firstCastDifference = FirstDifference(group.Select(unit =>
                (unit.Time, "casts", (double?)unit.Game.Casts, (double?)unit.Simulated.Casts))),
            firstStatDifference = FirstDifference(group.SelectMany(unit =>
                Enum.GetValues<Stat>().Select(stat => (unit.Time, stat.ToString(),
                    (double?)unit.Game.Stats[(int)stat], (double?)unit.Simulated.Stats[(int)stat])))),
            firstCooldownDifference = FirstDifference(group.SelectMany(unit => new[]
            {
                (unit.Time, "effectiveCooldownSeconds", unit.Game.EffectiveCooldownSeconds, unit.Simulated.EffectiveCooldownSeconds),
                (unit.Time, "cooldownProgressSeconds", unit.Game.CooldownProgressSeconds, unit.Simulated.CooldownProgressSeconds),
                (unit.Time, "pendingCasts", (double?)unit.Game.PendingCasts, (double?)unit.Simulated.PendingCasts)
            }))
        }).ToArray()
    };
}

static object[] UnmatchedSlots(IEnumerable<(double Time, GameBattleFrameSide Present, GameBattleFrameSide Other)> samples) =>
    samples.SelectMany(sample => sample.Present.Units.Select(unit => unit.Slot)
            .Except(sample.Other.Units.Select(unit => unit.Slot)).Select(slot => (slot, sample.Time)))
        .GroupBy(sample => sample.slot).OrderBy(group => group.Key)
        .Select(group => (object)new { slot = group.Key, firstSeenSeconds = group.First().Time }).ToArray();

static object? FirstDifference(IEnumerable<(double Time, string Field, double? Game, double? Simulation)> values)
{
    foreach (var (time, field, game, simulation) in values)
        if (game is { } observed && simulation is { } predicted && Math.Abs(observed - predicted) > 1e-6)
            return new { seconds = time, field, game = observed, simulated = predicted };
    return null;
}

internal sealed record FrameObservation(long? Index, GameBattleFrame Frame);
