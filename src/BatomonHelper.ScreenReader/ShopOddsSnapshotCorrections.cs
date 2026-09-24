using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.ScreenReader;

public static class ShopOddsSnapshotCorrections
{
    private static readonly Rarity[] RibbonOrder = [Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.SuperRare, Rarity.Legendary];

    public static ShopOddsSnapshot Apply(ShopOddsSnapshot source, IEnumerable<RecognizedField> fields, CatalogSnapshot catalog)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.OrdinalIgnoreCase);
        var odds = RibbonOrder.Select(rarity => new CapturedRarityOdds(rarity, Percentage($"{rarity}Odds", source.RarityOdds.First(current => current.Rarity == rarity).Probability))).ToImmutableArray();
        var trainerName = values.TryGetValue("Trainer", out var trainer) && !string.IsNullOrWhiteSpace(trainer) ? trainer : source.Trainer;
        var canonicalTrainer = ResolveTrainer(trainerName, catalog)?.Name ?? trainerName;
        var itemName = values.TryGetValue("ShopItem", out var item) && !string.IsNullOrWhiteSpace(item) ? item : source.ItemOffer?.Name;
        var canonicalItem = ResolveItem(itemName, catalog);
        var itemPrice = Integer("ShopItemPrice", source.ItemOffer?.Price ?? 0);
        return source with
        {
            Trainer = canonicalTrainer,
            ShopRank = Integer("ShopRank", source.ShopRank),
            Gold = Integer("Gold", source.Gold),
            RarityOdds = odds,
            ItemUsesRemaining = Integer("ItemUsesRemaining", source.ItemUsesRemaining),
            DailyItemLimit = Integer("DailyItemLimit", source.DailyItemLimit),
            ItemOffer = itemName is null ? null : new(canonicalItem?.Name ?? itemName, itemPrice, canonicalItem?.RuleText ?? source.ItemOffer?.RuleText),
            CapturedAt = DateTimeOffset.UtcNow
        };

        int Integer(string name, int fallback) => values.TryGetValue(name, out var raw) && int.TryParse(raw, out var value) ? value : fallback;
        decimal Percentage(string name, decimal fallback) => values.TryGetValue(name, out var raw) && decimal.TryParse(raw.TrimEnd('%'), out var value) && value is >= 0m and <= 100m ? value / 100m : fallback;
    }

    private static TrainerDefinition? ResolveTrainer(string? raw, CatalogSnapshot catalog) => string.IsNullOrWhiteSpace(raw) || catalog.Trainers.IsDefaultOrEmpty ? null : catalog.Trainers.FirstOrDefault(trainer =>
        trainer.Id.Equals(raw, StringComparison.OrdinalIgnoreCase) || trainer.Name.Equals(raw, StringComparison.OrdinalIgnoreCase));

    private static ItemDefinition? ResolveItem(string? raw, CatalogSnapshot catalog) => string.IsNullOrWhiteSpace(raw) || catalog.Items.IsDefaultOrEmpty ? null : catalog.Items.FirstOrDefault(item =>
        item.Id.Equals(raw, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(raw, StringComparison.OrdinalIgnoreCase));
}
