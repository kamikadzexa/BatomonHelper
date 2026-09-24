using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.ScreenReader;

public static class ShopOddsSnapshotCorrections
{
    public static ShopOddsSnapshot Apply(ShopOddsSnapshot source, IEnumerable<RecognizedField> fields, CatalogSnapshot catalog)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.OrdinalIgnoreCase);
        var rank = Integer("ShopRank", source.ShopRank);
        var odds = Enum.GetValues<Rarity>().Take(5).Select(rarity => new CapturedRarityOdds(rarity, Percentage($"{rarity}Odds", source.RarityOdds.First(current => current.Rarity == rarity).Probability))).ToImmutableArray();
        return source with
        {
            ShopRank = rank,
            Gold = Integer("Gold", source.Gold),
            RarityOdds = odds,
            ItemAvailable = Boolean("ItemAvailable", source.ItemAvailable),
            ItemOffer = null,
            CapturedAt = DateTimeOffset.UtcNow
        };

        int Integer(string name, int fallback) => values.TryGetValue(name, out var raw) && int.TryParse(raw, out var value) ? value : fallback;
        bool Boolean(string name, bool fallback) => values.TryGetValue(name, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;
        decimal Percentage(string name, decimal fallback) => values.TryGetValue(name, out var raw) && decimal.TryParse(raw.TrimEnd('%'), out var value) && value is >= 0m and <= 100m ? value / 100m : fallback;
    }
}
