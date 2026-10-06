using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BatomonHelper.ScreenReader;

public static class BoardSnapshotCorrections
{
    public static BoardSnapshot Apply(BoardSnapshot source, IEnumerable<RecognizedField> fields, CatalogSnapshot catalog)
    {
        var values = fields.ToDictionary(entry => entry.Name, entry => entry.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var board = source with
        {
            TrainerId = Text("TrainerId", source.TrainerId), Day = Number("Day", source.Day), Lives = Number("Lives", source.Lives),
            Wins = Number("Wins", source.Wins), WinTarget = Number("WinTarget", source.WinTarget), TeamHp = Number("TeamHp", source.TeamHp),
            Gold = Number("Gold", source.Gold), ShopRank = Number("ShopRank", source.ShopRank),
            RerollCost = values.ContainsKey("RerollMode") ? Mode() == "Paid" ? Number("RerollCount", source.RerollCost) : null : Number("RerollCost", source.RerollCost),
            FreeRerolls = values.ContainsKey("RerollMode") ? Mode() == "Free" ? Number("RerollCount", source.FreeRerolls) : null : Number("FreeRerolls", source.FreeRerolls),
            ItemUsesCounterVisible = CounterVisible(),
            ItemUsesRemaining = CounterVisible() ? Number("ItemUsesRemaining", source.ItemUsesRemaining) : null,
            ItemUsesMaximum = CounterVisible() ? Number("ItemUsesMaximum", source.ItemUsesMaximum) : null,
            ShopLocked = values.TryGetValue("ShopLocked", out var locked) ? bool.TryParse(locked, out var parsedLock) ? parsedLock : throw new FormatException("ShopLocked must be True or False.") : source.ShopLocked,
            RarityOdds = Enum.GetValues<Rarity>().Take(5).Select(rarity => new CapturedRarityOdds(rarity, Probability(rarity))).ToImmutableArray(),
            Team = Slots("Team", source.Team, false), Bench = Slots("Bench", source.Bench, false), Shop = Slots("Shop", source.Shop, true)
        };
        board = board with { Shop = board.Shop.Select(slot => slot with { Affordable = slot.Price is not null && board.Gold is not null ? slot.Price <= board.Gold : null }).ToImmutableArray() };
        var validation = BoardSnapshotValidator.Validate(board, catalog);
        if (!validation.IsValid) throw new FormatException(string.Join(" ", validation.Errors));
        return board;

        string Mode() => values["RerollMode"] is "Free" or "Paid" ? values["RerollMode"] : throw new FormatException("Choose Free or Paid rerolls.");
        bool CounterVisible() => values.TryGetValue("ItemUsesCounterVisible", out var text) ? bool.TryParse(text, out var visible) ? visible : throw new FormatException("ItemUsesCounterVisible must be True or False.") : source.ItemUsesCounterVisible;
        decimal Probability(Rarity rarity)
        {
            if (!values.TryGetValue($"{rarity}Odds", out var text)) return source.RarityOdds.FirstOrDefault(odd => odd.Rarity == rarity)?.Probability ?? -1;
            return decimal.TryParse(text.TrimEnd('%'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percentage)
                ? percentage / 100 : throw new FormatException($"{rarity} odds must be a percentage.");
        }

        string? Text(string name, string? fallback) => values.TryGetValue(name, out var text) ? string.IsNullOrWhiteSpace(text) ? null : text : fallback;
        int? Number(string name, int? fallback)
        {
            if (!values.TryGetValue(name, out var text)) return fallback;
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException($"{name} must be a non-negative integer.");
        }
        ImmutableArray<BoardSlot> Slots(string area, ImmutableArray<BoardSlot> slots, bool shop) => slots.Select(slot =>
        {
            var prefix = $"{area}[{slot.Index}]"; var occupant = Text(prefix + ".Occupant", slot.DefinitionId is null ? "empty" : (slot.Occupancy == SlotOccupancy.Item ? "item:" : "monster:") + slot.DefinitionId);
            if (occupant == "empty") return new BoardSlot(slot.Index, SlotOccupancy.Empty, Stats: []);
            if (occupant is null || !Regex.IsMatch(occupant, "^(monster|item):[a-z0-9-]+$")) throw new FormatException($"{prefix}: choose monster:id, item:id or empty.");
            var kind = occupant.StartsWith("item:", StringComparison.Ordinal) ? SlotOccupancy.Item : SlotOccupancy.Creature;
            var id = occupant[(occupant.IndexOf(':') + 1)..];
            var variant = kind == SlotOccupancy.Item ? ShinyState.Unknown : Enum.TryParse<ShinyState>(Text(prefix + ".Variant", slot.Variant.ToString()), true, out var shiny) ? shiny : ShinyState.Unknown;
            var stats = ImmutableArray.CreateBuilder<VisibleStat>();
            if (kind == SlotOccupancy.Creature)
            {
                var indices = values.Keys.Select(key => Regex.Match(key, "^" + Regex.Escape(prefix) + @"\.Stats\[(\d+)\]\.Kind$")).Where(match => match.Success).Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order().ToArray();
                foreach (var index in indices)
                {
                    var path = $"{prefix}.Stats[{index}]"; var kindText = Text(path + ".Kind", null);
                    if (kindText == "Absent") continue;
                    if (!Enum.TryParse<VisibleStatKind>(kindText, true, out var statKind)) throw new FormatException($"{path}: choose a visible stat type or Absent.");
                    stats.Add(new(statKind, Number(path + ".Value", null)));
                }
                if (indices.Length == 0 && !slot.Stats.IsDefault) stats.AddRange(slot.Stats);
            }
            return slot with { Occupancy = kind, DefinitionId = id, Variant = variant, Level = kind == SlotOccupancy.Creature && !shop ? Number(prefix + ".Level", slot.Level) : null,
                Stats = stats.ToImmutable(), Price = shop ? Number(prefix + ".Price", slot.Price) : null };
        }).ToImmutableArray();
    }
}
