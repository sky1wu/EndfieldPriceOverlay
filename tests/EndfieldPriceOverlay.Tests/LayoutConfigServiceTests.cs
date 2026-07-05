using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class LayoutConfigServiceTests
{
    [Fact]
    public void DefaultLayoutUsesMeasuredFourKCoordinates()
    {
        var service = new LayoutConfigService(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var layout = service.Effective(ScreenLayout.Default1080, 3840);

        Assert.Equal(ScreenLayout.Default4K, layout);
    }

    [Fact]
    public void CustomCalibrationIsNotOverridden()
    {
        var service = new LayoutConfigService();
        var custom = new ScreenLayout(new(0.1, 0.2, 0.3, 0.4), new(0.2, 0.3, 0.8, 0.7));

        Assert.Equal(custom, service.Effective(custom, 3840));
    }

    [Fact]
    public void MarketOverviewSlotsCoverSevenCardsThenFiveCards()
    {
        var first = MarketOverviewLayout.PriceSlot(0, 0.545);
        var seventh = MarketOverviewLayout.PriceSlot(6, 0.545);
        var eighth = MarketOverviewLayout.PriceSlot(7, 0.850);
        var last = MarketOverviewLayout.PriceSlot(11, 0.850);

        Assert.Equal(first.Left, eighth.Left, 6);
        Assert.Equal(first.Top, seventh.Top, 6);
        Assert.True(eighth.Top > first.Bottom);
        Assert.True(last.Right < 1);
        Assert.Equal(0.485, first.Top, 6);
        Assert.Equal(0.846, eighth.Bottom, 6);

        var firstName = MarketOverviewLayout.NameSlot(0, 0.545);
        Assert.True(firstName.Left < first.Left);
        Assert.True(firstName.Top > 0.545);
        Assert.True(firstName.Bottom > firstName.Top);
    }
}
