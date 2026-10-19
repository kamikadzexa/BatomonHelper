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
    public void Ranks_above_fourteen_are_normalized_to_the_maximum_rank()
    {
        var odds = ShopRankOdds.ForRank(15);

        Assert.True(ShopRankOdds.ForRank(14).SequenceEqual(odds));
        Assert.Equal(14, ShopRankOdds.Normalize(15));
        Assert.True(ShopOddsSnapshotValidator.Validate(new(null, ShopRankOdds.Normalize(15), 30, odds, false, null, DateTimeOffset.UtcNow), CatalogSnapshot.Empty).IsValid);
    }

    [Fact]
    public void Recognizes_only_an_exact_standard_ribbon()
    {
        Assert.True(ShopRankOdds.TryFindRank([30, 40, 25, 5, 0], out var exactRank));
        Assert.Equal(7, exactRank);
        Assert.False(ShopRankOdds.TryFindRank([99, 40, 25, 5, 0], out _));
    }
}
