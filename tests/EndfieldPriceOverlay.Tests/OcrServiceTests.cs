using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class OcrServiceTests
{
    [Fact]
    public void DisposeBeforeModelInitializationDoesNotThrow()
    {
        using var service = new OcrService(new LayoutConfigService());
    }

    [Theory]
    [InlineData(1486, 0.696, 486)]
    [InlineData(1486, 0.950, 1486)]
    [InlineData(1440, 0.999, 1440)]
    [InlineData(1635, 0.998, 1635)]
    public void CorrectsOnlyLowConfidenceLeadingOne(int recognized, double firstCharacterScore, int expected)
    {
        Assert.Equal(expected, OcrService.CorrectMarketPrice(recognized, firstCharacterScore));
    }

    [Theory]
    [InlineData("：剩余可购买数量400/400 18小时后+200△即将溢出", 400, 400, 200)]
    [InlineData("剩余可购买数量32O／96O 23小时后＋32O", 320, 960, 320)]
    [InlineData("剩余可购买数量 170 / 340 6小时后 + 170", 170, 340, 170)]
    public void ParsesPurchaseQuota(string text, int current, int limit, int dailyRecovery)
    {
        var reading = OcrService.ParsePurchaseQuota(text, 0.95);

        Assert.Equal(current, reading.Current);
        Assert.Equal(limit, reading.Limit);
        Assert.Equal(dailyRecovery, reading.DailyRecovery);
        Assert.Equal(0.95, reading.Confidence);
        Assert.True(reading.IsComplete);
    }
}
