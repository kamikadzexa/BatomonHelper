namespace BatomonHelper.Core;

public static class ShopStateValidator
{
    public static ValidationResult Validate(ShopState state, CatalogSnapshot catalog)
    {
        var errors = new List<string>();
        if (state.Hud.Day is < 1 or > 999) errors.Add("Day must be between 1 and 999.");
        if (state.Hud.Rank is < 1 or > 99) errors.Add("Rank must be between 1 and 99.");
        if (state.Team.Length > 6 || state.Bench.Length > 4 || state.Shop.Length > 5) errors.Add("The captured shop has an invalid slot count.");
        var ids = catalog.Monsters.Select(monster => monster.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (catalog.Monsters.Length > 0)
        {
            foreach (var unit in state.Team.Concat(state.Bench))
            {
                if (unit.Level is < 1 or > 4) errors.Add($"{unit.MonsterId} has an invalid level.");
                if (!ids.Contains(unit.MonsterId)) errors.Add($"Unknown monster id: {unit.MonsterId}.");
            }
        }
        return errors.Count == 0 ? ValidationResult.Success : new(false, [.. errors]);
    }
}
