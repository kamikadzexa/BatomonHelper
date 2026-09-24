using BatomonHelper.Core;

namespace BatomonHelper.Tests;

public sealed class ShopRankOddsTests
{
    [Fact]
    public void Rank_seven_uses_the_standard_ribbon()
    {
        var odds = ShopRankOdds.ForRank(7);

        Assert.Equal([.30m, .40m, .25m, .05m, 0m], odds.OrderBy(odds => odds.Rarity).Select(odds => odds.Probability));
    }

    [Fact]
    public void Recognizes_a_fixed_layout_ribbon_even_when_its_leading_read_is_wrong()
    {
        Assert.True(ShopRankOdds.TryFindRank([30, 40, 25, 5, 0], out var exactRank));
        Assert.Equal(7, exactRank);
        Assert.True(ShopRankOdds.TryFindRank([99, 40, 25, 5, 0], out var repairedRank));
        Assert.Equal(7, repairedRank);
    }
}
