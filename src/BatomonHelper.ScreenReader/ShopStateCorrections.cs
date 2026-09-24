using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.ScreenReader;

public static class ShopStateCorrections
{
    public static ShopState Apply(ShopState source, IEnumerable<RecognizedField> fields, CatalogSnapshot catalog)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.OrdinalIgnoreCase);
        var hud = source.Hud with
        {
            Day = Integer("Day", source.Hud.Day), Hearts = Integer("Hearts", source.Hud.Hearts),
            ProgressCurrent = Integer("ProgressCurrent", source.Hud.ProgressCurrent), ProgressTarget = Integer("ProgressTarget", source.Hud.ProgressTarget),
            HitPoints = Integer("HitPoints", source.Hud.HitPoints), Rank = Integer("Rank", source.Hud.Rank),
            RankProgress = Integer("RankProgress", source.Hud.RankProgress), RankProgressTarget = Integer("RankProgressTarget", source.Hud.RankProgressTarget),
            RerollCost = Integer("RerollCost", source.Hud.RerollCost), UpgradeCost = Integer("UpgradeCost", source.Hud.UpgradeCost),
            IsLocked = Boolean("IsLocked", source.Hud.IsLocked)
        };
        return source with
        {
            Hud = hud,
            Team = CorrectUnits("Team", source.Team, catalog),
            Bench = CorrectUnits("Bench", source.Bench, catalog),
            Shop = CorrectShop(source.Shop, catalog),
            TrainerId = values.TryGetValue("Trainer", out var trainer) && !string.IsNullOrWhiteSpace(trainer) ? trainer : source.TrainerId,
            ActiveSet = values.TryGetValue("ActiveSet", out var activeSet) && !string.IsNullOrWhiteSpace(activeSet) ? activeSet : source.ActiveSet,
            ItemUsesRemaining = Integer("ItemUsesRemaining", source.ItemUsesRemaining),
            Effects = source.Effects with
            {
                ResearchNotes = Integer("ResearchNotes", source.Effects.ResearchNotes),
                VipPass = Boolean("VipPass", source.Effects.VipPass),
                MysticIncenseQueued = Boolean("MysticIncenseQueued", source.Effects.MysticIncenseQueued),
                ForceAllMythical = Boolean("ForceAllMythical", source.Effects.ForceAllMythical),
                TicketRarity = ParseRarity("TicketRarity", source.Effects.TicketRarity),
                RainbowPearl = Boolean("RainbowPearl", source.Effects.RainbowPearl)
            },
            CapturedAt = DateTimeOffset.UtcNow
        };

        int Integer(string name, int fallback) => values.TryGetValue(name, out var raw) && int.TryParse(raw, out var value) ? value : fallback;
        bool Boolean(string name, bool fallback)
        {
            if (!values.TryGetValue(name, out var raw)) return fallback;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("locked", StringComparison.OrdinalIgnoreCase);
        }
        Rarity? ParseRarity(string name, Rarity? fallback) => values.TryGetValue(name, out var raw) && Enum.TryParse<Rarity>(raw, true, out var value) ? value : fallback;
        ImmutableArray<UnitState> CorrectUnits(string prefix, ImmutableArray<UnitState> units, CatalogSnapshot snapshot)
        {
            var fixedUnits = ImmutableArray.CreateBuilder<UnitState>();
            for (var index = 0; index < units.Length; index++)
            {
                var unit = units[index];
                var id = values.TryGetValue($"{prefix}{unit.Position + 1}", out var raw) ? ResolveMonster(raw, snapshot) : unit.MonsterId;
                fixedUnits.Add(unit with { MonsterId = id ?? unit.MonsterId, Level = Integer($"{prefix}{unit.Position + 1}Level", unit.Level) });
            }
            return fixedUnits.ToImmutable();
        }
        ImmutableArray<ShopSlotState> CorrectShop(ImmutableArray<ShopSlotState> slots, CatalogSnapshot snapshot)
        {
            var fixedSlots = ImmutableArray.CreateBuilder<ShopSlotState>();
            for (var index = 0; index < slots.Length; index++)
            {
                var slot = slots[index];
                var raw = values.TryGetValue($"Shop{index + 1}", out var supplied) ? supplied : null;
                var id = raw is null ? slot.OfferId : ResolveMonster(raw, snapshot);
                var kind = id is not null ? ShopSlotKind.Batomon : raw?.StartsWith("item:", StringComparison.OrdinalIgnoreCase) == true ? ShopSlotKind.Item : slot.Kind;
                var offer = id ?? (kind == ShopSlotKind.Item && raw is not null ? raw[5..].Trim() : slot.OfferId);
                fixedSlots.Add(slot with { Kind = kind, OfferId = offer });
            }
            return fixedSlots.ToImmutable();
        }
    }

    private static string? ResolveMonster(string raw, CatalogSnapshot catalog) => catalog.Monsters.FirstOrDefault(monster =>
        monster.Id.Equals(raw, StringComparison.OrdinalIgnoreCase) || monster.Name.Equals(raw, StringComparison.OrdinalIgnoreCase))?.Id;
}
