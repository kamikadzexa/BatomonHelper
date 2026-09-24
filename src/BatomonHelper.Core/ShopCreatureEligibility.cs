namespace BatomonHelper.Core;

/// <summary>Defines the base creatures that can be offered by the shop.</summary>
public static class ShopCreatureEligibility
{
    private static readonly HashSet<string> UnavailableIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ignit",
        "talonite",
        "wishwash"
    };

    public static bool IsShopCreature(MonsterDefinition monster) =>
        monster.BaseSpeciesId is null &&
        !UnavailableIds.Contains(monster.Id) &&
        !monster.Id.Contains("egg", StringComparison.OrdinalIgnoreCase);
}
