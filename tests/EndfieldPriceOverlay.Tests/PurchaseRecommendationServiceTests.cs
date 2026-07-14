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

    [Fact]
    public void TodayActualPriceFromUnsettledItemParticipatesInRecommendation()
    {
        var store = new CaptureStore(Path.Combine(directory, "today-actual.db"));
        store.Save(new CaptureReading(
            "武陵冻梨货组",
            [1959, 1707, 1980, 1492, 2964, 1098, 2400],
            new DateTime(2026, 2, 8, 12, 0, 0)));
        store.Save(new CaptureReading(
            "武陵冻梨货组",
            [2400, 2339, 2168, 1250, 1282, 2070, 1313],
            new DateTime(2026, 2, 14, 12, 0, 0)));
        store.SaveDailyPrices(
        [
            new DailyPriceReading(
                "息壤净水芯货组",
                486,
                new DateTime(2026, 2, 14, 12, 0, 0),
                ItemRegionCatalog.Wuling),
        ]);
        var service = new PurchaseRecommendationService(
            store,
            new PredictionStatusService(store, new PricePredictionService()));

        var result = service.Build(
            [new RegionPurchaseSettings(ItemRegionCatalog.Wuling, 170, 340, 170)],
            new DateOnly(2026, 2, 14));

        var region = Assert.Single(result);
        Assert.True(region.IsReady);
        var today = Assert.Single(region.Lines, line => line.Date == new DateOnly(2026, 2, 14));
        Assert.Equal("息壤净水芯货组", today.ItemName);
        Assert.Equal(486, today.Price);
        Assert.Equal(170, today.Quantity);
        Assert.Contains("1/9 个确定预测物资", region.Message);
    }

    [Fact]
    public void UnsettledFutureRangesParticipateUsingTheirMaximumPrice()
    {
        var store = new CaptureStore(Path.Combine(directory, "future-ranges.db"));
        SaveDaily(store, "武侠电影货组",
        [
            new(new DateOnly(2026, 6, 28), 3502),
            new(new DateOnly(2026, 6, 29), 2532),
            new(new DateOnly(2026, 6, 30), 1202),
            new(new DateOnly(2026, 7, 1), 2254),
            new(new DateOnly(2026, 7, 2), 1200),
            new(new DateOnly(2026, 7, 3), 4295),
            new(new DateOnly(2026, 7, 4), 1000),
            new(new DateOnly(2026, 7, 5), 3502),
            new(new DateOnly(2026, 7, 6), 2201),
            new(new DateOnly(2026, 7, 7), 2174),
            new(new DateOnly(2026, 7, 8), 1334),
            new(new DateOnly(2026, 7, 9), 2400),
            new(new DateOnly(2026, 7, 10), 1806),
            new(new DateOnly(2026, 7, 11), 2400),
            new(new DateOnly(2026, 7, 12), 1467),
            new(new DateOnly(2026, 7, 13), 2201),
        ], ItemRegionCatalog.Wuling);
        var service = new PurchaseRecommendationService(
            store,
            new PredictionStatusService(store, new PricePredictionService()));

        var result = service.Build(
            [new RegionPurchaseSettings(ItemRegionCatalog.Wuling, 170, 340, 170)],
            new DateOnly(2026, 7, 13));

        var region = Assert.Single(result);
        Assert.True(region.IsReady);
        Assert.Contains("1 个未收敛物资的价格上限", region.Message);
        var wednesday = Assert.Single(
            region.Lines,
            line => line.Date == new DateOnly(2026, 7, 15));
        Assert.Equal("武侠电影货组", wednesday.ItemName);
        Assert.True(wednesday.IsRange);
        Assert.Equal(711, wednesday.Minimum);
        Assert.Equal(1334, wednesday.Maximum);
        Assert.Equal(1334, wednesday.Price);
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
        IReadOnlyList<KeyValuePair<DateOnly, int>> prices,
        string region = ItemRegionCatalog.ValleyIv)
    {
        foreach (var (date, price) in prices)
        {
            store.SaveDailyPrices(
            [
                new DailyPriceReading(
                    itemName,
                    price,
                    date.ToDateTime(new TimeOnly(12, 0)),
                    region),
            ]);
        }
    }
}
