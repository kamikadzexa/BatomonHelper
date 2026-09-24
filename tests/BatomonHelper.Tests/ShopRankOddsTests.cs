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
    public void Recognizes_only_an_exact_standard_ribbon()
    {
        Assert.True(ShopRankOdds.TryFindRank([30, 40, 25, 5, 0], out var exactRank));
        Assert.Equal(7, exactRank);
        Assert.False(ShopRankOdds.TryFindRank([99, 40, 25, 5, 0], out _));
    }
}
