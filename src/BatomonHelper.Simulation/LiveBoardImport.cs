using BatomonHelper.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatomonHelper.Simulation;

public sealed record LiveBoardProjection(RulesManifest Rules, FrozenBoard Board, Unit?[]? Bench = null);

/// <summary>Imports pre-aura game stats; combat applies its own ongoing and battle-start effects once.</summary>
public static class LiveBoardImport
{
    public static LiveBoardProjection Create(BoardSnapshot board, GameCombatState data, RulesManifest rules)
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
        var bag = data.Trinkets.Select(id => GameCatalogIds.Resolve(id, rules.Trinkets.Select(value => value.Id)))
            .GroupBy(id => id).Select(group => new BagEntry(group.Key, group.Count())).ToArray();
        if (bag.Any(entry => !rules.Trinkets.Any(value => value.Id == entry.Id)))
            throw new InvalidDataException("A live trinket has no simulation rules.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { rules.Hash, data })));
        var calibratedRules = rules with { Species = species.Values.ToArray(), Hash = hash };
        var frozen = new FrozenBoard(board.Day!.Value, board.TeamHp!.Value, trainer, team, bag, [],
            Rank: board.ShopRank!.Value, Wins: board.Wins!.Value);
        return new(calibratedRules, frozen, bench);

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
                    AddedTypes = detail.AddedTypes,
                    Bonuses = detail.PermanentStats.Select((value, index) => value - basis.Stats[index]).ToArray()
                };
                // Live base cooldowns also cover game/wiki balance differences. Each species/level/variant has one basis.
                var levels = shiny && definition.Shiny.Length > 0 ? definition.Shiny : definition.Normal;
                var calibrated = levels.Select(value => value.Level == level ? value with { Cooldown = detail.BaseCooldown } : value).ToArray();
                species[definition.Id] = shiny && definition.Shiny.Length > 0
                    ? definition with { Shiny = calibrated } : definition with { Normal = calibrated };
            }
        }
    }
}
