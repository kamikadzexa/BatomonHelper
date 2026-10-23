using System.Collections.Immutable;
using System.Text.Json;
using BatomonHelper.Core;
using BatomonHelper.Simulation;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: RunValidator <combat-rules.json> <run.json> [run.json ...]");
    return 2;
}
var rules = JsonSerializer.Deserialize<RulesManifest>(File.ReadAllText(args[0]))!;
var reports = new List<object>();
var failures = 0;
foreach (var path in args.Skip(1))
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var run = document.RootElement;
    var boards = 0;
    foreach (var change in run.GetProperty("changes").EnumerateArray())
    {
        var board = change.GetProperty("board").Deserialize<BoardSnapshot>(GameStateBridge.JsonOptions)!;
        var combat = change.GetProperty("combat").Deserialize<GameCombatState>(GameStateBridge.JsonOptions);
        if (combat is null) continue;
        LiveBoardImport.Create(board with { Team = Normalize(board.Team), Bench = Normalize(board.Bench) }, combat, rules);
        boards++;
    }
    var fights = new List<object>();
    foreach (var fight in run.GetProperty("fights").EnumerateArray())
    {
        var start = fight.GetProperty("start").Deserialize<GameBattleState>(GameStateBridge.JsonOptions)!;
        var player = Import(start.Player, rules);
        var enemy = Import(start.Opponent, player.Rules);
        var resolver = new Combat(enemy.Rules);
        var simulated = resolver.ResolveBattle(StateSnapshots.Copy(player.Board), StateSnapshots.Copy(enemy.Board), 69);
        var reference = resolver.ResolveBattle(StateSnapshots.Copy(player.Board), StateSnapshots.Copy(enemy.Board), 69, reference: true);
        var outcome = fight.GetProperty("outcome").Deserialize<GameBattleOutcome>(GameStateBridge.JsonOptions);
        var matches = outcome is null ? (bool?)null : outcome.PlayerWon == simulated.PlayerWon;
        var agrees = simulated.PlayerWon == reference.PlayerWon && Math.Abs(simulated.Seconds - reference.Seconds) < .001
            && Math.Abs(simulated.PlayerHp - reference.PlayerHp) < .001 && Math.Abs(simulated.OpponentHp - reference.OpponentHp) < .001;
        if (matches == false || !agrees) failures++;
        fights.Add(new { day = start.Player.Day, gameWon = outcome?.PlayerWon, simulatedWon = simulated.PlayerWon,
            outcomeMatches = matches, referenceAgrees = agrees, simulatedSeconds = simulated.Seconds,
            samples = fight.GetProperty("frames").GetArrayLength(),
            trainerProgressRecorded = start.Player.Combat.Counters is not null && start.Player.Combat.ExtraTrainers is not null });
    }
    reports.Add(new { file = Path.GetFileName(path), version = run.GetProperty("version").GetInt32(),
        build = run.GetProperty("gameBuild").GetString(), importedShopBoards = boards, fights });
}
Console.WriteLine(JsonSerializer.Serialize(new { seed = 69, analyzerVersion = BoardAnalyzer.Version, rulesHash = rules.Hash, reports, failures,
    limitations = new[] { "Outcome agreement does not prove exact timeline parity; recordings omit the game's random seed.",
        "Version 2 runs omit trainer progress and additional trainers. Their player frame slots must be reversed (5 - slot) for timeline comparisons; version 3 uses screen order." }
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

ImmutableArray<BoardSlot> Normalize(ImmutableArray<BoardSlot> slots) => slots.IsDefault ? [] : slots.Select(slot => slot with
{
    DefinitionId = slot.DefinitionId is null ? null : GameCatalogIds.Resolve(slot.DefinitionId, rules.Species.Select(species => species.Id))
}).ToImmutableArray();

LiveBoardProjection Import(GameBattleSide side, RulesManifest input) => LiveBoardImport.Create(new BoardSnapshot
{
    Day = side.Day, TeamHp = side.TeamHp, TrainerId = side.TrainerId, ShopRank = side.Rank, Wins = side.Wins, Team = Normalize(side.Team)
}, side.Combat with { Bench = null }, input);
