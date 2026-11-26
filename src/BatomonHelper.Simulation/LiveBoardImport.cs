using BatomonHelper.Core;

namespace BatomonHelper.Simulation;

public sealed record LiveBoardProjection(RulesManifest Rules, FrozenBoard Board, Unit?[]? Bench = null);

/// <summary>Imports pre-aura game stats; combat applies its own ongoing and battle-start effects once.</summary>
public static class LiveBoardImport
{
    public static LiveBoardProjection CreateBattleSide(GameBattleSide side, RulesManifest rules, bool mirrored = false) =>
        Create(new BoardSnapshot { Day = side.Day, TeamHp = side.TeamHp, TrainerId = side.TrainerId,
            Team = side.Team, ShopRank = side.Rank, Wins = side.Wins }, side.Combat with { Bench = null },
            battleStart: true, rules: rules, mirrored: mirrored);

    public static LiveBoardProjection Create(BoardSnapshot board, GameCombatState data, RulesManifest rules, bool battleStart = false, bool mirrored = false)
    {
        var occupied = board.Team.Where(slot => slot.Occupancy == SlotOccupancy.Creature).ToArray();
        ValidateMetadata(occupied, data.Team, "team");
        var team = new Unit?[6];
        var species = rules.Species.ToDictionary(value => value.Id);
        Import(occupied, data.Team, team, 0);
        Unit?[]? bench = null;
        if (data.Bench is not null)
        {
            var occupiedBench = board.Bench.Where(slot => slot.Occupancy == SlotOccupancy.Creature).ToArray();
            ValidateMetadata(occupiedBench, data.Bench, "bench");
            bench = new Unit?[4];
            Import(occupiedBench, data.Bench, bench, 6);
        }
        var trainer = GameCatalogIds.Resolve(board.TrainerId!, rules.Trainers.Select(value => value.Id));
        if (!rules.Trainers.Any(value => value.Id == trainer)) throw new InvalidDataException($"No simulation trainer {board.TrainerId}.");
        var trinkets = rules.Trinkets.ToDictionary(value => GameCatalogIds.Normalize(value.Id), value => value.Id);
        var bag = data.Trinkets.Select((rawId, index) => trinkets.TryGetValue(GameCatalogIds.Normalize(rawId), out var id)
            ? new BagEntry(id, 1, index + 1) : throw new InvalidDataException($"No simulation rules for live trinket {rawId}.")).ToArray();
        var extraTrainers = (data.ExtraTrainers ?? []).Select(id => GameCatalogIds.Resolve(id, rules.Trainers.Select(value => value.Id))).ToArray();
        if (extraTrainers.Any(id => !rules.Trainers.Any(value => value.Id == id)) ||
            data.Counters?.Any(pair => !double.IsFinite(pair.Value)) == true)
            throw new InvalidDataException("Invalid live trainer progress.");
        var frozen = new FrozenBoard(board.Day!.Value, board.TeamHp!.Value, trainer, team, bag, extraTrainers,
            Counters: data.Counters is null ? null : new(data.Counters), Rank: ShopRankOdds.Normalize(board.ShopRank!.Value), Wins: board.Wins!.Value,
            Mirrored: mirrored, IncludesOngoingHp: battleStart);
        if (!battleStart) frozen = frozen with { MaxHp = new Combat(rules).RecoverBaseHp(frozen) };
        return new(rules, frozen, bench);

        void ValidateMetadata(BoardSlot[] slots, GameCombatUnit[] metadata, string area)
        {
            if (metadata.Length != slots.Length || metadata.Select(unit => unit.Slot).Distinct().Count() != metadata.Length ||
                metadata.Any(unit => !slots.Any(slot => slot.Index == unit.Slot)))
                throw new InvalidDataException($"Combat metadata does not match the {area} positions.");
        }

        void Import(BoardSlot[] slots, GameCombatUnit[] metadata, Unit?[] destination, int instanceOffset)
        {
            foreach (var slot in slots)
            {
                var detail = metadata.Single(unit => unit.Slot == slot.Index);
                if (detail.PermanentStats.Length != 9 || detail.PermanentStats.Any(value => !double.IsFinite(value)) ||
                    detail.BaseCooldown is { } cooldown && (!double.IsFinite(cooldown) || cooldown <= 0))
                    throw new InvalidDataException("Invalid live combat stats.");
                if (!species.TryGetValue(slot.DefinitionId!, out var definition))
                    throw new InvalidDataException($"No simulation rules for {slot.DefinitionId}.");
                var shiny = slot.Variant == ShinyState.Shiny;
                var level = slot.Level!.Value;
                var basis = LevelRules.Resolve(definition, level, shiny);
                destination[slot.Index] = new Unit
                {
                    Instance = instanceOffset + slot.Index + 1, SpeciesId = definition.Id, Level = level, Shiny = shiny,
                    AddedTypes = detail.AddedTypes, CapturedCooldown = new(definition.Id, level, shiny, detail.BaseCooldown),
                    Bonuses = detail.PermanentStats.Select((value, index) => value - basis.Stats[index]).ToArray()
                };
            }
        }
    }
}
