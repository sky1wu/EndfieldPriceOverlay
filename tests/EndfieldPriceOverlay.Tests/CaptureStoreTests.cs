using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;
using System.IO;

namespace EndfieldPriceOverlay.Tests;

public sealed class CaptureStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"endfield-{Guid.NewGuid():N}");

    [Fact]
    public void ExistingPythonSchemaMapsOldestPriceToSixDaysAgo()
    {
        var store = new CaptureStore(Path.Combine(directory, "prices.db"));
        store.Save(new CaptureReading(
            "天师龙泡泡货组",
            [1400, 2605, 2343, 2755, 1360, 1218, 3416],
            new DateTime(2026, 7, 4, 12, 0, 0)));

        var values = store.GetDatedPrices("天师龙泡泡货组");

        Assert.Equal(1400, values[new DateOnly(2026, 6, 28)]);
        Assert.Equal(3416, values[new DateOnly(2026, 7, 4)]);
    }

    [Fact]
    public void LatestCaptureWinsForSameDate()
    {
        var store = new CaptureStore(Path.Combine(directory, "prices.db"));
        store.Save(new CaptureReading("商品", [1000, 1100, 1200, 1300, 1400, 1500, 1600], new DateTime(2026, 7, 4, 12, 0, 0)));
        store.Save(new CaptureReading("商品", [1001, 1101, 1201, 1301, 1401, 1501, 1601], new DateTime(2026, 7, 4, 13, 0, 0)));

        Assert.Equal(1601, store.GetDatedPrices("商品")[new DateOnly(2026, 7, 4)]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
