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
    public void RepeatedCompleteWeeksRemainUncertainLikeTheWebTool()
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

        Assert.Equal(PredictionState.Filtering, status.State);
        Assert.Empty(status.Future);
        Assert.NotEmpty(status.Ranges);
    }

    [Fact]
    public void WuxiaMovieDataKeepsTheSameThreeTrendsAsTheWebTool()
    {
        var store = new CaptureStore(Path.Combine(directory, "wuxia-movie.db"));
        store.ApplyPriceChanges("武侠电影货组",
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
        ]);
        var service = new PredictionStatusService(store, new PricePredictionService());

        var status = service.Get("武侠电影货组", new DateOnly(2026, 7, 13));

        Assert.Equal(PredictionState.Filtering, status.State);
        Assert.Contains("还剩 3 种", status.Message);
        Assert.Collection(
            status.Ranges,
            tuesday => Assert.Equal((2174, 4659), (tuesday.Minimum, tuesday.Maximum)),
            wednesday => Assert.Equal((711, 1334), (wednesday.Minimum, wednesday.Maximum)),
            thursday => Assert.Equal((2400, 7200), (thursday.Minimum, thursday.Maximum)),
            friday => Assert.Equal((602, 1806), (friday.Minimum, friday.Maximum)),
            saturday => Assert.Equal((2400, 9600), (saturday.Minimum, saturday.Maximum)),
            sunday => Assert.Equal((367, 1467), (sunday.Minimum, sunday.Maximum)));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
