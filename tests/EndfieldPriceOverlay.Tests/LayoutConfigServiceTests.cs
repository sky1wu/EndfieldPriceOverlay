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
}
