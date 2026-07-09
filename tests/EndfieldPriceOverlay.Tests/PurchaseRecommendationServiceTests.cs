using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class PurchaseRecommendationServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"endfield-purchase-{Guid.NewGuid():N}");

    [Fact]
    public void PlanMaximizesQuantityAndAvoidsExpensiveOverflowDay()
    {
        var store = new CaptureStore(Path.Combine(directory, "prices.db"));
        SaveDaily(store, "悬空鼷兽骨雕货组",
        [
            new(new DateOnly(2026, 6, 28), 704),
            new(new DateOnly(2026, 6, 29), 1800),
            new(new DateOnly(2026, 6, 30), 2268),
            new(new DateOnly(2026, 7, 1), 2600),
            new(new DateOnly(2026, 7, 2), 2029),
            new(new DateOnly(2026, 7, 3), 3357),
            new(new DateOnly(2026, 7, 4), 1289),
            new(new DateOnly(2026, 7, 5), 1507),
            new(new DateOnly(2026, 7, 6), 1800),
            new(new DateOnly(2026, 7, 7), 2268),
            new(new DateOnly(2026, 7, 8), 2600),
            new(new DateOnly(2026, 7, 9), 2029),
        ]);
        var service = new PurchaseRecommendationService(
            store,
            new PredictionStatusService(store, new PricePredictionService()));

        var result = service.Build(
            [new RegionPurchaseSettings(ItemRegionCatalog.ValleyIv, 640, 960, 320)],
            new DateOnly(2026, 7, 9));

        var region = Assert.Single(result);
        Assert.True(region.IsReady);
        Assert.Equal(1600, region.TotalQuantity);
        Assert.Collection(
            region.Lines,
            thursday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 9), thursday.Date);
                Assert.Equal(320, thursday.Quantity);
                Assert.Equal(2029, thursday.Price);
            },
            saturday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 11), saturday.Date);
                Assert.Equal(960, saturday.Quantity);
                Assert.Equal(1289, saturday.Price);
            },
            sunday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 12), sunday.Date);
                Assert.Equal(320, sunday.Quantity);
                Assert.Equal(1507, sunday.Price);
            });
    }

    [Fact]
    public void MissingPredictionsReturnsBlockingMessage()
    {
        var store = new CaptureStore(Path.Combine(directory, "empty.db"));
        var service = new PurchaseRecommendationService(
            store,
            new PredictionStatusService(store, new PricePredictionService()));

        var result = service.Build(
            [new RegionPurchaseSettings(ItemRegionCatalog.Wuling, 320, 960, 320)],
            new DateOnly(2026, 7, 9));

        var region = Assert.Single(result);
        Assert.False(region.IsReady);
        Assert.Equal(0, region.TotalQuantity);
        Assert.Contains("还没有价格记录", region.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static void SaveDaily(
        CaptureStore store,
        string itemName,
        IReadOnlyList<KeyValuePair<DateOnly, int>> prices)
    {
        foreach (var (date, price) in prices)
        {
            store.SaveDailyPrices(
            [
                new DailyPriceReading(
                    itemName,
                    price,
                    date.ToDateTime(new TimeOnly(12, 0)),
                    ItemRegionCatalog.ValleyIv),
            ]);
        }
    }
}
