using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class PredictionCalibrationRegressionTests
{
    private readonly PricePredictionService service = new();

    [Fact]
    public void RepeatingRoundedHistoryDoesNotChangeAnyOf512TemplateSequences()
    {
        double?[] epsilon = [0.02515, -0.04123, 0.01787, -0.03269, 0.05871, -0.04537, 0.00931];
        foreach (var first in PricePredictionService.Templates)
        foreach (var second in PricePredictionService.Templates)
        foreach (var target in PricePredictionService.Templates)
        {
            var week1 = new WeekRecord("W1", service.Predict(epsilon, first).Cast<int?>().ToArray());
            var week2 = new WeekRecord("W2", service.Predict(epsilon, second).Cast<int?>().ToArray());
            var actual = service.Predict(epsilon, target);
            var current = new WeekRecord("Current", actual.Select((price, i) => i < 3 ? (int?)price : null).ToArray());
            var control = Survivors(service.AnalyzeSafe([week1, week2, current])!, current.Prices);
            var repeated = Survivors(service.AnalyzeSafe([week1, week2, week2 with { Week = "W3" }, current])!, current.Prices);

            Assert.Equal(Curves(control), Curves(repeated));
            Assert.NotEmpty(repeated);
            // All remaining curves must predict the unseen prices within the
            // mathematically bounded source/target integer-rounding error.
            Assert.All(repeated, prediction =>
            {
                for (var i = 3; i < 7; i++)
                {
                    Assert.InRange(Math.Abs(prediction.Prices[i] - actual[i]), 0, PricePredictionService.RoundingTolerance);
                    Assert.InRange(actual[i], prediction.MinimumAt(i), prediction.MaximumAt(i));
                }
            });
        }
    }

    [Fact]
    public void HistoryOrderAndAnIncompleteLeadingWeekCannotPoisonCalibration()
    {
        double?[] epsilon = [0.02515, -0.04123, 0.01787, -0.03269, 0.05871, -0.04537, 0.00931];
        var first = new WeekRecord("W1", service.Predict(epsilon, PricePredictionService.Templates[6]).Cast<int?>().ToArray());
        var second = new WeekRecord("W2", service.Predict(epsilon, PricePredictionService.Templates[5]).Cast<int?>().ToArray());
        var actual = service.Predict(epsilon, PricePredictionService.Templates[3]);
        var current = new WeekRecord("Current", actual.Select((price, i) => i < 3 ? (int?)price : null).ToArray());
        var original = Survivors(service.AnalyzeSafe([first, second, current])!, current.Prices);
        var reordered = Survivors(service.AnalyzeSafe([
            new("Partial", [null, null, null, null, null, null, 3502]), second, first, current])!, current.Prices);

        Assert.Equal(Curves(original), Curves(reordered));
    }

    [Fact]
    public void AllBadCandidatesAreRejectedInsteadOfKeepingTheLeastBad()
    {
        var template = PricePredictionService.Templates[0];
        PricePrediction[] predictions = [new(template, template, new double?[7], [1000, 1000, 1000, 1000, 1000, 1000, 1000])];
        int?[] actual = [3000, 3000, null, null, null, null, null];

        Assert.Empty(service.FilterPredictions(predictions, actual));
        var rejected = Assert.Single(service.FilterCurrentWeek(predictions, actual));
        Assert.True(rejected.Eliminated);
        Assert.Equal(0, rejected.Confidence);
        Assert.Empty(service.FilterCurrentWeek([], actual));
    }

    [Fact]
    public void ExactMatchDoesNotGiveImperfectRivalsPerfectConfidence()
    {
        var template = PricePredictionService.Templates[0];
        int[] exact = [1600, 2000, 2200, 2600, 3000, 3200, 3400];
        int[] close = [1602, 2000, 2200, 2600, 3000, 3200, 3400];
        var scored = service.FilterCurrentWeek([
            new(template, template, new double?[7], exact),
            new(template, template, new double?[7], close),
        ], exact.Cast<int?>().ToArray());

        Assert.Equal(1, scored[0].Confidence);
        Assert.InRange(scored[1].Confidence!.Value, 0, 0.999);
        Assert.False(scored[1].Eliminated);
    }

    [Fact]
    public void ContradictoryCompleteHistoryRevokesTheCalibration()
    {
        var result = service.AnalyzeSafe([
            new("W1", [1600, 2000, 2200, 2600, 3000, 3200, 3400]),
            new("W2", [501, 502, 503, 504, 505, 506, 507]),
        ]);

        Assert.NotNull(result);
        Assert.Equal(AnalysisStage.Pending, result.Stage);
        Assert.Equal(0, result.CandidateCount);
        Assert.Empty(result.EightPredictions);
    }

    private PricePrediction[] Survivors(AnalysisResult result, int?[] current) => service.FilterCurrentWeek(
        result.Stage == AnalysisStage.Locked ? result.EightPredictions
            : result.CandidateForecasts?.SelectMany(candidate => candidate.Possibilities) ?? [], current)
        .Where(prediction => !prediction.Eliminated).ToArray();

    private static string[] Curves(IEnumerable<PricePrediction> predictions) => predictions
        .Select(prediction => string.Join(',', prediction.Prices) + ":" + string.Join(',', prediction.MinimumPrices ?? [])
            + ":" + string.Join(',', prediction.MaximumPrices ?? []))
        .Distinct().Order(StringComparer.Ordinal).ToArray();
}
