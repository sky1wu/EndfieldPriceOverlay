using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class PricePredictionServiceTests
{
    private readonly PricePredictionService service = new();

    [Fact]
    public void FormulaRoundTripRecoversEpsilon()
    {
        double?[] epsilon = [0.02, -0.01, 0.03, -0.02, 0.01, 0, -0.03];
        var prices = service.Predict(epsilon, PricePredictionService.Templates[0]);
        var recovered = service.ComputeEpsilon(prices.Cast<int?>().ToArray(), PricePredictionService.Templates[0]);

        for (var index = 0; index < 7; index++)
        {
            Assert.Equal(epsilon[index]!.Value, recovered[index]!.Value, 10);
        }
    }

    [Fact]
    public void PredictionClampsEpsilonLikeTheWebTool()
    {
        var prices = service.Predict(Enumerable.Repeat<double?>(1, 7).ToArray(), PricePredictionService.Templates[0]);

        Assert.Equal([5600, 7000, 7700, 9100, 10500, 11200, 11900], prices);
    }

    [Fact]
    public void OneWeekProducesSixtyFourPossibilities()
    {
        var week = service.Predict(Enumerable.Repeat<double?>(0.01, 7).ToArray(), PricePredictionService.Templates[2]);
        var result = service.Analyze([new WeekRecord("W1", week.Cast<int?>().ToArray())]);

        Assert.NotNull(result);
        Assert.Equal(AnalysisStage.FirstWeek, result.Stage);
        Assert.Equal(64, result.Predictions.Count);
    }

    [Fact]
    public void CrossWeekDataLocksEpsilonAndCurrentTemplate()
    {
        double?[] epsilon = [0.02, -0.01, 0.03, -0.02, 0.01, 0, -0.03];
        var week1 = service.Predict(epsilon, PricePredictionService.Templates[0]);
        var week2 = service.Predict(epsilon, PricePredictionService.Templates[3]);
        var currentFull = service.Predict(epsilon, PricePredictionService.Templates[6]);
        int?[] current = [currentFull[0], currentFull[1], null, null, null, null, null];

        var result = service.AnalyzeSafe(
        [
            new WeekRecord("W1", week1.Cast<int?>().ToArray()),
            new WeekRecord("W2", week2.Cast<int?>().ToArray()),
            new WeekRecord("W3", current),
        ]);

        Assert.NotNull(result);
        Assert.Equal(AnalysisStage.Locked, result.Stage);
        var survivors = service.FilterCurrentWeek(result.EightPredictions, current)
            .Where(item => !item.Eliminated)
            .ToArray();
        var survivor = Assert.Single(survivors);
        Assert.Equal(7, survivor.TargetTemplate.Id);
        Assert.Equal(currentFull, survivor.Prices);
    }

    [Fact]
    public void ScreenshotRegressionPredictsMissingSunday()
    {
        int?[] source = [1959, 1707, 1980, 1492, 2964, 1098, 2400];
        int?[] observed = [2339, 2168, 1250, 1282, 2070, 1216, null];
        var result = service.AnalyzeSafe([new WeekRecord("W1", source), new WeekRecord("W2", observed)]);

        Assert.NotNull(result);
        Assert.Equal(AnalysisStage.Locked, result.Stage);
        var survivor = Assert.Single(
            service.FilterCurrentWeek(result.EightPredictions, observed),
            item => !item.Eliminated);
        Assert.Equal(3, survivor.TargetTemplate.Id);
        Assert.Equal(2200, survivor.Prices[6]);
    }

    [Fact]
    public void RepeatedCompleteWeeksKeepAllEpsilonCandidatesLikeTheWebTool()
    {
        int?[] repeated = [1800, 2268, 2600, 2029, 3357, 1289, 1507];

        var result = service.AnalyzeSafe(
        [
            new WeekRecord("W1", repeated),
            new WeekRecord("W2", repeated),
        ]);

        Assert.NotNull(result);
        Assert.Equal(AnalysisStage.Pending, result.Stage);
        Assert.Equal(8, result.CandidateCount);
        Assert.Equal(8, result.CandidateForecasts?.Count);
    }
}
