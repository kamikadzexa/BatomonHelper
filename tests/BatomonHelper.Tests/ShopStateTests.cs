using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.Tests;

public sealed class ShopStateTests
{
    [Fact]
    public void Projects_explicit_item_uses_instead_of_inventory_length()
    {
        var state = new ShopState(
            new ShopHud(3, 8, 1, 10, 300, 4, 0, 0, 3, 30, false),
            null, "starter", [], [], [new(ShopSlotKind.Item, "voucher", null, 5, false)], ["voucher"], 2, new(), DateTimeOffset.UtcNow);

        var odds = state.ToOddsInput();

        Assert.Equal(2, odds.ItemUsesRemaining);
        Assert.True(odds.HasEligibleItemOffer);
        Assert.Equal(4, odds.ShopRank);
    }

    [Fact]
    public void Uses_shop_state_as_an_odds_source()
    {
        var catalog = new CatalogSnapshot("test", "unknown", null, DateTimeOffset.UtcNow, [], "hash", [],
            [new("spinarai", "Spinarai", Rarity.Common, null, ImmutableHashSet.Create("starter"), true)]);
        var input = new ShopOddsInput(1, 1, "starter", [], [], [], new(), 0, false);

        var result = new OddsCalculator().Calculate(catalog, input);

        Assert.Equal(5, result.BatomonSlotsNextShop);
        Assert.Single(result.MonsterOdds);
    }
}
