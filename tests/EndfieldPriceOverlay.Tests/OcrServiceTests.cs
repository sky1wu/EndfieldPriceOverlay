using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class OcrServiceTests
{
    [Fact]
    public void DisposeBeforeModelInitializationDoesNotThrow()
    {
        using var service = new OcrService(new LayoutConfigService());
    }
}
