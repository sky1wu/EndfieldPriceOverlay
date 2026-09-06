using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class PredictionReplayRegressionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"endfield-replay-{Guid.NewGuid():N}");
    private static readonly DateOnly Monday = new(2026, 6, 29);
    private static readonly int[] BoneRiseFall = [1800, 2268, 2600, 2029, 3357, 1289, 1507];
    private static readonly int[] BoneFallRise = [2400, 2268, 1600, 947, 1579, 992, 1657];
    private static readonly int[] BoneOscillation = [2200, 1625, 3000, 734, 3545, 630, 2817];
    private const string Item = "悬空鼷兽骨雕货组";

    [Fact]
    public void HistoricalDateDoesNotReadTheRestOfThatWeekOrLaterWeeks()
    {
        var complete = Store("complete");
        var prefix = Store("prefix");
        SaveWeek(complete, 0, BoneRiseFall);
        SaveWeek(prefix, 0, BoneRiseFall);
        SaveWeek(complete, 1, BoneRiseFall);
        SaveWeek(complete, 2, BoneFallRise);
        SaveWeek(prefix, 1, BoneRiseFall.Take(3));
        var day = Monday.AddDays(9);

        var expected = Status(prefix, day);
        var actual = Status(complete, day);

        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Future, actual.Future);
        Assert.Equal(expected.Ranges, actual.Ranges);
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public void MissingCurrentWeekIsNotReportedAsAModelConflict()
    {
        var store = Store("not-recorded");
        SaveWeek(store, 0, BoneRiseFall);
        SaveWeek(store, 1, BoneFallRise);

        var status = Status(store, Monday.AddDays(14));

        Assert.Equal(PredictionState.Pending, status.State);
        Assert.Contains("尚未记录", status.Message);
        Assert.Empty(status.Future);
        Assert.Empty(status.Ranges);
    }

    [Fact]
    public void MondayIsNotReadyEvenWhenOnlyOneHistoricalCurveMatches()
    {
        var store = Store("monday");
        SaveWeek(store, 0, BoneRiseFall);
        SaveWeek(store, 1, BoneFallRise);
        SaveWeek(store, 2, BoneRiseFall.Take(1));

        var status = Status(store, Monday.AddDays(14));

        Assert.NotEqual(PredictionState.Ready, status.State);
        Assert.Empty(status.Future);
    }

    [Fact]
    public void UnseenExtremeIsWithheldRatherThanClippedToAnInventedLimit()
    {
        var store = Store("unseen-extreme");
        SaveWeek(store, 0, BoneRiseFall);
        SaveWeek(store, 1, BoneFallRise);
        SaveWeek(store, 2, BoneOscillation.Take(3));

        var status = Status(store, Monday.AddDays(16));

        Assert.NotEqual(PredictionState.Ready, status.State);
        Assert.Empty(status.Future);
        Assert.DoesNotContain(status.Ranges, range => range.Date == Monday.AddDays(19));
        Assert.Contains("无法可靠外推", status.Message);
    }

    [Fact]
    public void ObservedSameTemplateExtremeIsRecoveredOnItsNextOccurrence()
    {
        var store = Store("known-extreme");
        SaveWeek(store, 0, BoneRiseFall);
        SaveWeek(store, 1, BoneFallRise);
        SaveWeek(store, 2, BoneOscillation);
        SaveWeek(store, 3, BoneOscillation.Take(3));

        var status = Status(store, Monday.AddDays(23));

        Assert.Equal(PredictionState.Ready, status.State);
        Assert.Equal(630, Assert.Single(status.Future, point => point.Date == Monday.AddDays(26)).Price);
        Assert.All(status.Future, point => Assert.InRange(point.Price, 300, 5500));
    }

    [Fact]
    public void BadCurrentReadingRevokesReadyAndDoesNotReachPurchasePlanning()
    {
        var store = Store("bad-reading");
        SaveWeek(store, 0, BoneRiseFall);
        SaveWeek(store, 1, BoneRiseFall.Take(4));
        var day = Monday.AddDays(10);
        Assert.Equal(PredictionState.Ready, Status(store, day).State);
        store.ApplyPriceChanges(Item, [new(day, 501)]);

        var status = Status(store, day);
        Assert.Equal(PredictionState.Pending, status.State);
        Assert.Empty(status.Future);
        Assert.Empty(status.Ranges);
        Assert.Empty(status.CandidateTrends);
        var purchase = new PurchaseRecommendationService(store, new PredictionStatusService(store, new PricePredictionService()));
        var region = Assert.Single(purchase.Build([new(ItemRegionCatalog.ValleyIv, 640, 960, 320)], day));
        Assert.False(region.IsReady);
        Assert.Empty(region.Lines);
    }

    [Fact]
    public void ExplicitReferenceRangeCannotBePassedAsAPurchaseCeiling()
    {
        var store = Store("range-plan");
        var service = new PurchaseRecommendationService(store, new PredictionStatusService(store, new PricePredictionService()));
        var result = service.BuildRegionPlan(new(ItemRegionCatalog.ValleyIv, 320, 960, 320),
            [new(ItemRegionCatalog.ValleyIv, Item, Monday, 0, 2000, 1000, 2000)], [Monday], 1);

        Assert.False(result.IsReady);
        Assert.Empty(result.Lines);
    }

    private CaptureStore Store(string name) => new(Path.Combine(directory, name + ".db"));
    private static PredictionStatus Status(CaptureStore store, DateOnly day) =>
        new PredictionStatusService(store, new PricePredictionService()).Get(Item, day);

    private static void SaveWeek(CaptureStore store, int week, IEnumerable<int> prices)
    {
        foreach (var (price, i) in prices.Select((price, i) => (price, i)))
        {
            store.SaveDailyPrices([new(Item, price, Monday.AddDays(week * 7 + i).ToDateTime(new TimeOnly(12, 0)), ItemRegionCatalog.ValleyIv)]);
        }
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid scratch directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
