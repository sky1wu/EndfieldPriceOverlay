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
}
