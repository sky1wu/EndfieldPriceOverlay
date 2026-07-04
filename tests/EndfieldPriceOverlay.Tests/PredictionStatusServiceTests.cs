using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class PredictionStatusServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"endfield-status-{Guid.NewGuid():N}");

    [Fact]
    public void RegressionDataProducesSundayPrediction()
    {
        var store = new CaptureStore(Path.Combine(directory, "prices.db"));
        store.Save(new CaptureReading(
            "测试商品",
            [1959, 1707, 1980, 1492, 2964, 1098, 2400],
            new DateTime(2026, 2, 8, 12, 0, 0)));
        store.Save(new CaptureReading(
            "测试商品",
            [2400, 2339, 2168, 1250, 1282, 2070, 1216],
            new DateTime(2026, 2, 14, 12, 0, 0)));
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("测试商品", new DateOnly(2026, 2, 14));

        Assert.Equal(PredictionState.Ready, status.State);
        var sunday = Assert.Single(status.Future);
        Assert.Equal(new DateOnly(2026, 2, 15), sunday.Date);
        Assert.Equal(2200, sunday.Price);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
