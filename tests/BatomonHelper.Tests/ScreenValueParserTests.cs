using BatomonHelper.Capture;

namespace BatomonHelper.Tests;

public sealed class ScreenValueParserTests
{
    [Fact]
    public void ParsesCoreValuesAndNormalizesLockText()
    {
        var values = ScreenValueParser.Parse([
            ("DAY 4", .9d), ("Rank 2", .9d), ("$13", .9d),
            ("Free Rerolls: 1", .9d), ("Locked: on", .9d), ("Shop count: 5", .9d)
        ]);

        Assert.Equal(4, values["Day"].Value);
        Assert.Equal(2, values["ShopRank"].Value);
        Assert.Equal(13, values["Gold"].Value);
        Assert.Equal(1, values["IsShopLocked"].Value);
        Assert.True(values["ShopCount"].IsValid);
    }

    [Fact]
    public void MarksImpossibleValuesForConfirmation()
    {
        var values = ScreenValueParser.Parse([("Gold: -5", .9d), ("Shop count: 8", .9d)]);

        Assert.False(values.ContainsKey("Gold"));
        Assert.False(values["ShopCount"].IsValid);
        Assert.Equal("Needs confirmation", values["ShopCount"].Status);
    }

    [Fact]
    public void GuideMovesThroughTheControlledActionsInOrder()
    {
        Assert.Equal(CaptureStep.Reroll, CaptureGuide.Next(CaptureStep.Baseline));
        Assert.Equal(CaptureStep.Buy, CaptureGuide.Next(CaptureStep.Reroll));
        Assert.Equal("Apply lock filter", CaptureGuide.For(CaptureStep.Lock).PrimaryAction);
        Assert.Contains("Restart Batomon", CaptureGuide.For(CaptureStep.RestartValidation).GameInstruction);
    }
}
