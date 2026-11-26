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
        var observed = fight.GetProperty("frames").EnumerateArray().Select(entry =>
            entry.GetProperty("frame").Deserialize<GameBattleFrame>(GameStateBridge.JsonOptions)!).ToArray();
        if (version == 2)
            observed = observed.Select(frame => frame with
            {
                Player = frame.Player with { Units = frame.Player.Units.Select(unit => unit with { Slot = 5 - unit.Slot }).ToArray() }
            }).ToArray();

        var timeline = new List<GameBattleFrame>();
        var simulated = resolver.ResolveBattle(player.Board, enemy.Board, 69, audit: timeline.Add);
        var reference = resolver.ResolveBattle(player.Board, enemy.Board, 69, reference: true);
        var outcome = fight.GetProperty("outcome").Deserialize<GameBattleOutcome>(GameStateBridge.JsonOptions);
        var matches = outcome is null ? (bool?)null : outcome.PlayerWon == simulated.PlayerWon;
        var agrees = simulated.PlayerWon == reference.PlayerWon && Math.Abs(simulated.Seconds - reference.Seconds) < .001
            && Math.Abs(simulated.PlayerHp - reference.PlayerHp) < .001 && Math.Abs(simulated.OpponentHp - reference.OpponentHp) < .001;
        if (matches == false || !agrees) failures++;
        var distinct = observed.GroupBy(frame => JsonSerializer.Serialize(frame, GameStateBridge.JsonOptions)).Select(group => group.First()).ToArray();
        // The last state at a timestamp includes any battle-start effects sampled at 0s.
        var samples = distinct.GroupBy(frame => frame.ElapsedSeconds).Select(group => group.Last()).OrderBy(frame => frame.ElapsedSeconds).ToArray();
        var aligned = samples.Where(frame => frame.ElapsedSeconds <= simulated.Seconds + 1e-7)
            .Select(frame => (Game: frame, Simulated: timeline.Last(sim => sim.ElapsedSeconds <= frame.ElapsedSeconds + 1e-7))).ToArray();
        var terminal = distinct.FirstOrDefault(frame => frame.Player.Health <= 0 || frame.Opponent.Health <= 0);
        var latestAlive = distinct.Where(frame => frame.Player.Health > 0 && frame.Opponent.Health > 0).MaxBy(frame => frame.ElapsedSeconds);
        fights.Add(new
        {
            day = start.Player.Day, gameWon = outcome?.PlayerWon, simulatedWon = simulated.PlayerWon,
            outcomeMatches = matches, referenceAgrees = agrees, simulatedSeconds = simulated.Seconds,
            startingHp = new { player = start.Player.TeamHp, opponent = start.Opponent.TeamHp },
            samples = observed.Length, distinctStates = distinct.Length, comparedSamples = aligned.Length,
            terminalObserved = terminal is not null, recordedTerminalSeconds = terminal?.ElapsedSeconds,
            lastBothAliveSeconds = latestAlive?.ElapsedSeconds,
            simulatedEndedBeforeRecordedSurvival = latestAlive is not null && simulated.Seconds + .001 < latestAlive.ElapsedSeconds,
            trainerProgressRecorded = start.Player.Combat.Counters is not null && start.Player.Combat.ExtraTrainers is not null,
            timeline = new { player = Compare(aligned.Select(x => (x.Game.Player, x.Simulated.Player))),
                opponent = Compare(aligned.Select(x => (x.Game.Opponent, x.Simulated.Opponent))) }
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
        "Simulation values come from the last completed event at or before the sample. Intervals without a terminal sample give a lower bound on fight duration.",
        "Per-unit damage compares direct cast output. Game totals may also attribute status damage.",
        "In versions 2 and 3, the field named gameBuild records the helper catalog build. Version 2 player frame slots are reversed." }
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

ImmutableArray<BoardSlot> Normalize(ImmutableArray<BoardSlot> slots) => slots.IsDefault ? [] : slots.Select(slot => slot with
{
    DefinitionId = slot.DefinitionId is null ? null : GameCatalogIds.Resolve(slot.DefinitionId, rules.Species.Select(species => species.Id))
}).ToImmutableArray();

static object? Compare(IEnumerable<(GameBattleFrameSide Game, GameBattleFrameSide Simulated)> pairs)
{
    var samples = pairs.ToArray();
    if (samples.Length == 0) return null;
    var units = samples.SelectMany(sample => sample.Game.Units.Join(sample.Simulated.Units, game => game.Slot, sim => sim.Slot,
        (game, sim) => (Game: game, Simulated: sim))).ToArray();
    return new
    {
        maxHpError = samples.Max(sample => Math.Abs(sample.Game.Health - sample.Simulated.Health)),
        maxStartingHpError = samples.Max(sample => Math.Abs(sample.Game.MaximumHealth - sample.Simulated.MaximumHealth)),
        maxShieldError = samples.Max(sample => Math.Abs(sample.Game.Shield - sample.Simulated.Shield)),
        maxBurnError = samples.Max(sample => Math.Abs(sample.Game.Burn - sample.Simulated.Burn)),
        maxPoisonError = samples.Max(sample => Math.Abs(sample.Game.Poison - sample.Simulated.Poison)),
        maxShockError = samples.Max(sample => Math.Abs(sample.Game.Shock - sample.Simulated.Shock)),
        bySlot = units.GroupBy(unit => unit.Game.Slot).OrderBy(group => group.Key).Select(group => new
        {
            slot = group.Key,
            maxCooldownSpeedError = group.Max(unit => Math.Abs(unit.Game.Stats[(int)Stat.Speed] - unit.Simulated.Stats[(int)Stat.Speed])),
            maxCastError = group.Max(unit => Math.Abs(unit.Game.Casts - unit.Simulated.Casts)),
            maxDamageOutputError = group.Max(unit => Math.Abs(unit.Game.DamageDealt - unit.Simulated.DamageDealt)),
            maxHealingOutputError = group.Max(unit => Math.Abs(unit.Game.HealingDone - unit.Simulated.HealingDone)),
            maxShieldOutputError = group.Max(unit => Math.Abs(unit.Game.ShieldGranted - unit.Simulated.ShieldGranted))
        }).ToArray()
    };
}
