using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

static class Diagnostics
{
    public static object Trace(string item, string cutoffText, SortedDictionary<DateOnly, int> truth,
        PricePredictionService service)
    {
        var cutoff = DateOnly.Parse(cutoffText);
        var records = truth.Where(p => p.Key <= cutoff)
            .GroupBy(p => p.Key.AddDays(-(((int)p.Key.DayOfWeek + 6) % 7)))
            .OrderBy(g => g.Key).Select(g =>
            {
                var prices = new int?[7];
                foreach (var (day, price) in g) prices[((int)day.DayOfWeek + 6) % 7] = price;
                return new WeekRecord(g.Key.ToString("yyyy-MM-dd"), prices);
            }).ToArray();
        var result = service.AnalyzeSafe(records)!;
        return new
        {
            Item = item, Cutoff = cutoffText, Records = records, Result = result,
            // Pairwise projections help inspect the data; the calibrated model
            // now uses whole-history assignments rather than intersecting rounds.
            PairwiseReference = Rounds(records, service),
            CompletePairwiseReference = Rounds(records.Where(r => r.Prices.All(p => p is not null)).ToArray(), service),
            Filtered = service.FilterCurrentWeek(result.Stage == AnalysisStage.Locked
                ? result.EightPredictions
                : result.CandidateForecasts?.SelectMany(c => c.Possibilities) ?? [], records[^1].Prices),
        };
    }

    private static object[] Rounds(WeekRecord[] records, PricePredictionService service) => records
        .Zip(records.Skip(1), (source, target) => new
        {
            SourceWeek = source.Week, TargetWeek = target.Week,
            Matches = service.FilterPredictions(
                    service.EnumerateAllPredictions(service.EnumerateEpsilonCandidates(source.Prices)), target.Prices)
                .GroupBy(p => p.SourceTemplate.Id).Select(g => g.MinBy(p => p.Residual)!)
                .Select(p => new { Source = p.SourceTemplate.Id, Target = p.TargetTemplate.Id, p.Residual, p.Epsilon }).ToArray(),
        }).Cast<object>().ToArray();

    public static object Synthetic(PricePredictionService service)
    {
        double?[] epsilon = [0.02515, -0.04123, 0.01787, -0.03269, 0.05871, -0.04537, 0.00931];
        var exact = service.Predict(epsilon, PricePredictionService.Templates[0]);
        var nearby = (int[])exact.Clone();
        nearby[0] += 10;
        var confidence = service.FilterCurrentWeek([
            new(PricePredictionService.Templates[0], PricePredictionService.Templates[0], epsilon, exact),
            new(PricePredictionService.Templates[0], PricePredictionService.Templates[0], epsilon, nearby),
        ], exact.Cast<int?>().ToArray()).Select(p => new { p.Residual, p.Confidence, p.Eliminated });
        return new { Repeated = Sequences(service, epsilon, true), Control = Sequences(service, epsilon, false),
            ConfidenceProbe = confidence };
    }

    private static object Sequences(PricePredictionService service, double?[] epsilon, bool repeat)
    {
        var cases = new List<object>();
        var ready = 0;
        var wrongReady = 0;
        var wrongReadyOver5Percent = 0;
        var excludedTruth = 0;
        // Model-generated integer prices, including its normal rounding. A repeated middle
        // week is intentionally included: equal weeks have zero self-match residual under
        // every source template, even when that source template is wrong for that week.
        foreach (var first in PricePredictionService.Templates)
        foreach (var second in PricePredictionService.Templates)
        foreach (var third in PricePredictionService.Templates)
        {
            var week1 = service.Predict(epsilon, first).Cast<int?>().ToArray();
            var week2 = service.Predict(epsilon, second).Cast<int?>().ToArray();
            var actual = service.Predict(epsilon, third);
            var current = actual.Select((p, i) => i < 3 ? (int?)p : null).ToArray();
            var records = new List<WeekRecord> { new("W1", week1), new("W2", week2) };
            if (repeat) records.Add(new("W3", week2));
            records.Add(new("Current", current));
            var result = service.AnalyzeSafe(records)!;
            var candidates = result.Stage == AnalysisStage.Locked ? result.EightPredictions
                : result.CandidateForecasts?.SelectMany(p => p.Possibilities).ToArray() ?? [];
            var survivors = service.FilterCurrentWeek(candidates, current).Where(p => !p.Eliminated)
                .DistinctBy(p => string.Join(',', p.Prices)).ToArray();
            var covered = survivors.Any(p => p.Prices.Skip(3).Zip(actual.Skip(3))
                .All(pair => Math.Abs(pair.First - pair.Second) <= 1));
            if (!covered) excludedTruth++;
            if (survivors.Length == 1)
            {
                ready++;
                if (!covered) wrongReady++;
                if (survivors[0].Prices.Skip(3).Zip(actual.Skip(3))
                    .Any(pair => Math.Abs(pair.First / (double)pair.Second - 1) > .05)) wrongReadyOver5Percent++;
            }
            if (!covered && cases.Count < 5) cases.Add(new
            {
                Templates = repeat ? new[] { first.Id, second.Id, second.Id, third.Id }
                    : new[] { first.Id, second.Id, third.Id }, Actual = actual,
                Predicted = survivors.Select(p => p.Prices).ToArray(), result.Stage,
                result.CandidateCount, Source = result.LockedSourceTemplate?.Id,
            });
        }

        return new { TemplateSequences = 512, Epsilon = epsilon, RepeatedMiddleWeek = repeat,
            PrefixDays = 3, Ready = ready, WrongReady = wrongReady, WrongReadyOver5Percent = wrongReadyOver5Percent,
            ExcludedTruth = excludedTruth, Examples = cases };
    }
}
