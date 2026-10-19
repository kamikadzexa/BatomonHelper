using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatomonHelper.Core;

public sealed record GameStateMessage(int ProtocolVersion, long Sequence, DateTimeOffset UpdatedAt,
    int ProcessId, string Phase, BoardSnapshot? Board, string? Error = null, GameCombatState? Combat = null,
    GameBattleState? Battle = null);

public sealed record GameBattleSide(int Day, int TeamHp, string TrainerId,
    System.Collections.Immutable.ImmutableArray<BoardSlot> Team, GameCombatState Combat, int Rank = 1, int Wins = 0);
public sealed record GameBattleState(GameBattleSide Player, GameBattleSide Opponent, GameBattleOutcome? Outcome = null);
public sealed record GameBattleOutcome(bool PlayerWon);

public sealed record GameCombatUnit(int Slot, double[] PermanentStats, double? BaseCooldown, string[] AddedTypes);
public sealed record GameCombatState(GameCombatUnit[] Team, string[] Trinkets, GameCombatUnit[]? Bench = null);

public static class GameCatalogIds
{
    public static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static string Resolve(string id, IEnumerable<string> candidates)
    {
        var matches = candidates.Where(candidate => Normalize(candidate) == Normalize(id)).ToArray();
        return matches.Length == 1 ? matches[0] : id;
    }
}

/// <summary>Reads the companion mod's atomic snapshot. Never attaches to or captures the game.</summary>
public sealed class GameStateBridge
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Godot", "app_userdata", "Batomon Showdown", "batomon-helper", "state.json");
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<GameStateMessage> ReadAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(DefaultPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Bridge snapshot exceeds 1 MB.");
        using var reader = new StreamReader(stream);
        return Parse(await reader.ReadToEndAsync(cancellationToken), catalog, DateTimeOffset.UtcNow);
    }

    public static GameStateMessage Parse(string json, CatalogSnapshot catalog, DateTimeOffset now)
    {
        var message = JsonSerializer.Deserialize<GameStateMessage>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty bridge snapshot.");
        if (message.ProtocolVersion != 1) throw new InvalidDataException("Unsupported bridge protocol. Update the helper and companion mod together.");
        if (message.ProcessId <= 0 || message.Sequence < 1 || now - message.UpdatedAt > TimeSpan.FromSeconds(5)
            || message.UpdatedAt - now > TimeSpan.FromSeconds(5))
            throw new InvalidDataException("Game state is stale. Start the game with the BatomonHelper bridge mod enabled.");
        if (message.Error is { Length: > 0 }) throw new InvalidDataException(message.Error);
        if (message.Phase == "other") return message with { Board = null };
        if (message.Phase == "battle")
        {
            var battle = message.Battle ?? throw new InvalidDataException("The bridge did not supply battle-start teams.");
            var player = ResolveSide(battle.Player);
            var opponent = ResolveSide(battle.Opponent);
            if (player.Day != opponent.Day) throw new InvalidDataException("Battle teams have different days.");
            return message with { Board = null, Battle = battle with { Player = player, Opponent = opponent } };
        }
        if (message.Phase != "shop") throw new InvalidDataException("Unknown bridge phase.");
        var board = message.Board ?? throw new InvalidDataException("The bridge did not supply a shop board.");
        if ((board.CapturedAt - message.UpdatedAt).Duration() > TimeSpan.FromSeconds(5))
            throw new InvalidDataException("Board timestamp does not match the game heartbeat.");
        // Godot emits binary floating point. Remove only its tiny decimal sum residue.
        var odds = board.RarityOdds;
        if (!odds.IsDefaultOrEmpty && odds.Length == 5 && odds.All(value => value.Probability is >= 0 and <= 1))
        {
            var residue = 1m - odds.Sum(value => value.Probability);
            if (residue != 0 && Math.Abs(residue) <= .000000000001m)
            {
                var largest = Enumerable.Range(0, odds.Length).MaxBy(index => odds[index].Probability);
                board = board with { RarityOdds = odds.SetItem(largest, odds[largest] with { Probability = odds[largest].Probability + residue }) };
            }
        }
        // Game IDs and wiki IDs differ for a few renamed creatures. Resolve only unique exact aliases.
        board = board with
        {
            ShopRank = board.ShopRank is int rank ? ShopRankOdds.Normalize(rank) : null,
            TrainerId = ResolveTrainer(board.TrainerId),
            Team = Resolve(board.Team),
            Bench = Resolve(board.Bench),
            Shop = Resolve(board.Shop)
        };
        var validation = BoardSnapshotValidator.Validate(board, catalog);
        if (!validation.IsValid) throw new InvalidDataException("Game state could not be applied: " + string.Join(" ", validation.Errors));
        return message with { Board = board };

        GameBattleSide ResolveSide(GameBattleSide side)
        {
            var trainer = ResolveTrainer(side.TrainerId);
            var team = Resolve(side.Team);
            if (side.Day is < 1 or > 999 || side.TeamHp <= 0 ||
                !catalog.Trainers.Any(value => value.Id == trainer) || side.Combat is null ||
                team.Length != 6 || !team.Select(slot => slot.Index).SequenceEqual(Enumerable.Range(0, 6)) ||
                team.Any(slot => slot.Occupancy != SlotOccupancy.Empty &&
                    (slot.Occupancy != SlotOccupancy.Creature || slot.Level is null or < 1 or > 4 ||
                     slot.Variant is not (ShinyState.Normal or ShinyState.Shiny) ||
                     !catalog.Monsters.Any(value => value.Id == slot.DefinitionId))))
                throw new InvalidDataException("Invalid battle-start team.");
            return side with { TrainerId = trainer!, Team = team };
        }

        System.Collections.Immutable.ImmutableArray<BoardSlot> Resolve(System.Collections.Immutable.ImmutableArray<BoardSlot> slots)
        {
            if (slots.IsDefault) throw new InvalidDataException("Missing bridge slots.");
            return [.. slots.Select(slot =>
            {
                if (slot.DefinitionId is null) return slot;
                if (slot.Occupancy == SlotOccupancy.Item)
                    return slot with { DefinitionId = GameCatalogIds.Resolve(slot.DefinitionId, catalog.Items.Select(item => item.Id)) };
                if (slot.Occupancy != SlotOccupancy.Creature) return slot;
                var matches = catalog.Monsters.Where(monster => CreatureNames.Matches(monster, slot.DefinitionId)).ToArray();
                return matches.Length == 1 ? slot with { DefinitionId = matches[0].Id } : slot;
            })];
        }

        string? ResolveTrainer(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            var normalized = GameCatalogIds.Normalize(id);
            var candidates = catalog.Trainers.Where(trainer =>
                GameCatalogIds.Normalize(trainer.Id) == normalized || GameCatalogIds.Normalize(trainer.Name) == normalized).ToArray();
            if (candidates.Length == 1) return candidates[0].Id;

            // Game data has used a trainer_ prefix in some resource paths; do not apply a fuzzy match.
            const string prefix = "trainer";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                var stripped = normalized[prefix.Length..];
                candidates = catalog.Trainers.Where(trainer => GameCatalogIds.Normalize(trainer.Id) == stripped).ToArray();
                if (candidates.Length == 1) return candidates[0].Id;
            }
            // Resource-backed builds can expose a resource path rather than the bare ID.
            candidates = catalog.Trainers.Where(trainer => normalized.Contains(GameCatalogIds.Normalize(trainer.Id), StringComparison.Ordinal)).ToArray();
            if (candidates.Length == 1) return candidates[0].Id;
            return id;
        }
    }
}
