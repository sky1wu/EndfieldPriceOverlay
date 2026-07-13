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
        Assert.Equal(0, status.RequiredFutureDays);
        Assert.Collection(
            status.Future,
            today =>
            {
                Assert.Equal(new DateOnly(2026, 2, 14), today.Date);
                Assert.Equal(1216, today.Price);
            },
            sunday =>
            {
                Assert.Equal(new DateOnly(2026, 2, 15), sunday.Date);
                Assert.Equal(2200, sunday.Price);
            });
    }

    [Fact]
    public void ReadyPredictionIncludesTodayWhenTodayPriceIsMissing()
    {
        var store = new CaptureStore(Path.Combine(directory, "missing-today.db"));
        store.Save(new CaptureReading(
            "测试商品",
            [1959, 1707, 1980, 1492, 2964, 1098, 2400],
            new DateTime(2026, 2, 8, 12, 0, 0)));
        store.Save(new CaptureReading(
            "测试商品",
            [2400, 2339, 2168, 1250, 1282, 2070, 1216],
            new DateTime(2026, 2, 14, 12, 0, 0)));
        store.ApplyPriceChanges("测试商品", [new PriceRecordChange(new DateOnly(2026, 2, 14), null)]);
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("测试商品", new DateOnly(2026, 2, 14));

        Assert.Equal(PredictionState.Ready, status.State);
        Assert.Contains(status.Future, value => value.Date == new DateOnly(2026, 2, 14));
        var today = status.Future.Single(value => value.Date == new DateOnly(2026, 2, 14));
        Assert.Equal(1216, today.Price);
    }

    [Fact]
    public void EmptyHistoryReportsDaysUntilCompleteWeek()
    {
        var store = new CaptureStore(Path.Combine(directory, "empty-prices.db"));
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("尚未记录", new DateOnly(2026, 7, 1));

        Assert.Equal(PredictionState.Insufficient, status.State);
        Assert.Equal(4, status.RequiredFutureDays);
        Assert.Contains("未来 4 天", status.Message);
    }

    [Fact]
    public void FirstCompleteWeekReportsNextMondayWait()
    {
        var store = new CaptureStore(Path.Combine(directory, "first-week.db"));
        store.Save(new CaptureReading(
            "首次记录商品",
            [1600, 2000, 2200, 2600, 3000, 3200, 3400],
            new DateTime(2026, 7, 5, 12, 0, 0)));
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("首次记录商品", new DateOnly(2026, 7, 5));

        Assert.Equal(PredictionState.Insufficient, status.State);
        Assert.Equal(1, status.RequiredFutureDays);
        Assert.Contains("下周一", status.Message);
    }

    [Fact]
    public void DuplicateCandidateCurvesProduceReadyPrediction()
    {
        var store = new CaptureStore(Path.Combine(directory, "duplicate-curves.db"));
        store.ApplyPriceChanges("悬空鼷兽骨雕货组",
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
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("悬空鼷兽骨雕货组", new DateOnly(2026, 7, 9));

        Assert.Equal(PredictionState.Ready, status.State);
        Assert.Equal(0, status.RequiredFutureDays);
        Assert.DoesNotContain("8 个候选", status.Message);
        Assert.Collection(
            status.Future,
            thursday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 9), thursday.Date);
                Assert.Equal(2029, thursday.Price);
            },
            friday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 10), friday.Date);
                Assert.Equal(3357, friday.Price);
            },
            saturday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 11), saturday.Date);
                Assert.Equal(1289, saturday.Price);
            },
            sunday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 12), sunday.Date);
                Assert.Equal(1507, sunday.Price);
            });
    }

    [Fact]
    public void RepeatedCompleteWeeksLockNewWeekFromMondayPrice()
    {
        var store = new CaptureStore(Path.Combine(directory, "new-week-lock.db"));
        store.ApplyPriceChanges("悬空鼷兽骨雕货组",
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
            new(new DateOnly(2026, 7, 10), 3357),
            new(new DateOnly(2026, 7, 11), 1289),
            new(new DateOnly(2026, 7, 12), 1507),
            new(new DateOnly(2026, 7, 13), 1800),
        ]);
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("悬空鼷兽骨雕货组", new DateOnly(2026, 7, 13));

        Assert.Equal(PredictionState.Ready, status.State);
        Assert.Equal(0, status.RequiredFutureDays);
        Assert.Collection(
            status.Future,
            monday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 13), monday.Date);
                Assert.Equal(1800, monday.Price);
            },
            tuesday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 14), tuesday.Date);
                Assert.Equal(2268, tuesday.Price);
            },
            wednesday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 15), wednesday.Date);
                Assert.Equal(2600, wednesday.Price);
            },
            thursday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 16), thursday.Date);
                Assert.Equal(2029, thursday.Price);
            },
            friday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 17), friday.Date);
                Assert.Equal(3357, friday.Price);
            },
            saturday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 18), saturday.Date);
                Assert.Equal(1289, saturday.Price);
            },
            sunday =>
            {
                Assert.Equal(new DateOnly(2026, 7, 19), sunday.Date);
                Assert.Equal(1507, sunday.Price);
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
