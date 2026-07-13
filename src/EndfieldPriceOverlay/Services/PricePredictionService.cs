using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PricePredictionService
{
    public const double MatchThreshold = 0.003;

    public static IReadOnlyList<PriceTemplate> Templates { get; } =
    [
        new(1, "逐渐上升", [1600, 2000, 2200, 2600, 3000, 3200, 3400], 5),
        new(2, "逐渐下降", [2200, 2000, 1800, 1700, 1500, 1200, 1100], 5),
        new(3, "先降后升", [2400, 2000, 1600, 1400, 1600, 2000, 2200], 5),
        new(4, "先升后降", [1800, 2000, 2600, 3000, 3400, 2600, 2000], 5),
        new(5, "奇高小震", [2000, 1600, 2400, 1600, 2400, 1600, 2400], 4),
        new(6, "偶高小震", [2000, 2400, 1600, 2400, 1600, 2400, 1600], 4),
        new(7, "奇高大震", [2200, 1400, 3000, 1200, 3600, 1000, 4000], 6),
        new(8, "偶高大震", [2200, 3000, 1600, 3600, 1200, 4000, 1000], 6),
    ];

    public double?[] ComputeEpsilon(IReadOnlyList<int?> weekPrices, PriceTemplate template)
    {
        ValidateWeek(weekPrices);
        return weekPrices
            .Select((price, index) => price is null
                ? (double?)null
                : (price.Value / (double)template.Grid[index] - 1) / template.Coeff)
            .ToArray();
    }

    public int[] Predict(
        IReadOnlyList<double?> epsilon,
        PriceTemplate target,
        IReadOnlyList<double?>? fallback = null)
    {
        if (epsilon.Count != 7 || fallback is not null && fallback.Count != 7)
        {
            throw new ArgumentException("ε 向量必须包含周一至周日 7 个位置。");
        }

        return target.Grid.Select((grid, index) =>
        {
            var value = epsilon[index] ?? fallback?[index] ?? 0;
            return JavaScriptRound(grid * (1 + target.Coeff * value));
        }).ToArray();
    }

    public double MatchResidual(IReadOnlyList<int> predicted, IReadOnlyList<int?> actual)
    {
        ValidateWeek(actual);
        var sum = 0d;
        var count = 0;
        for (var index = 0; index < 7; index++)
        {
            if (actual[index] is not { } price)
            {
                continue;
            }

            var difference = (predicted[index] - price) / (double)price;
            sum += difference * difference;
            count++;
        }

        return count == 0 ? double.PositiveInfinity : sum / count;
    }

    public IReadOnlyList<EpsilonCandidate> EnumerateEpsilonCandidates(IReadOnlyList<int?> weekPrices) =>
        Templates.Select(template => new EpsilonCandidate(template, ComputeEpsilon(weekPrices, template))).ToArray();

    public IReadOnlyList<PricePrediction> EnumerateAllPredictions(IEnumerable<EpsilonCandidate> candidates) =>
        candidates.SelectMany(candidate => Templates.Select(target => new PricePrediction(
            candidate.SourceTemplate,
            target,
            candidate.Epsilon,
            Predict(candidate.Epsilon, target)))).ToArray();

    public IReadOnlyList<PricePrediction> FilterPredictions(
        IEnumerable<PricePrediction> predictions,
        IReadOnlyList<int?> weekPrices)
    {
        var scored = predictions
            .Select(item => item with { Residual = MatchResidual(item.Prices, weekPrices) })
            .OrderBy(item => item.Residual)
            .ToArray();
        if (scored.Length == 0)
        {
            return [];
        }

        var threshold = Math.Max(scored[0].Residual * 5, MatchThreshold);
        var filtered = scored.Where(item => item.Residual <= threshold).ToArray();
        return filtered.Length > 0 ? filtered : [scored[0]];
    }

    public IReadOnlyList<PricePrediction> FilterCurrentWeek(
        IEnumerable<PricePrediction> predictions,
        IReadOnlyList<int?> currentWeek)
    {
        var source = predictions.ToArray();
        if (currentWeek.All(price => price is null))
        {
            return source.Select(item => item with
            {
                Eliminated = false,
                Residual = double.PositiveInfinity,
                Confidence = null,
            }).ToArray();
        }

        var scored = source
            .Select(item => item with { Residual = MatchResidual(item.Prices, currentWeek) })
            .OrderBy(item => item.Residual)
            .ToArray();
        var minimum = scored[0].Residual;
        var threshold = Math.Max(minimum * 5, MatchThreshold);
        return scored.Select(item => item with
        {
            Eliminated = item.Residual > threshold,
            Confidence = minimum < 1e-9 ? 1 : Math.Min(1, minimum / item.Residual),
        }).ToArray();
    }

    public AnalysisResult? AnalyzeSafe(IReadOnlyList<WeekRecord> records)
    {
        if (records.Count == 0)
        {
            return null;
        }

        var complete = records.Where(record => record.Prices.All(price => price is not null)).ToArray();
        var nearComplete = records.Where(record =>
        {
            var missing = record.Prices.Count(price => price is null);
            return missing is >= 1 and <= 2;
        }).ToArray();

        if (complete.Length == 0 && nearComplete.Length == 0)
        {
            var result = Analyze(records);
            return result?.Stage == AnalysisStage.Locked
                ? result with { Stage = AnalysisStage.Pending }
                : result;
        }

        if (complete.Length == records.Count)
        {
            return Analyze(records);
        }

        var fitRecords = complete.Length > 0 ? complete : nearComplete;
        var fitResult = Analyze(fitRecords);
        var fullResult = Analyze(records);
        AnalysisResult? final;
        if (fullResult is null || fullResult.Stage == AnalysisStage.FirstWeek)
        {
            final = fitResult ?? Analyze(records);
        }
        else if (fitResult is null || fitResult.Stage == AnalysisStage.FirstWeek)
        {
            final = fullResult;
        }
        else
        {
            final = fullResult.CandidateCount <= fitResult.CandidateCount ? fullResult : fitResult;
        }

        return complete.Length == 0 && final?.Stage == AnalysisStage.Locked
            ? final with { Stage = AnalysisStage.Pending }
            : final;
    }

    public AnalysisResult? Analyze(IReadOnlyList<WeekRecord> records)
    {
        if (records.Count == 0)
        {
            return null;
        }

        foreach (var record in records)
        {
            ValidateWeek(record.Prices);
        }

        if (records.Count == 1)
        {
            return FirstWeek(records[0]);
        }

        var completeWeeks = records.Where(record => record.Prices.All(price => price is not null)).ToArray();
        if (completeWeeks.Length == 0)
        {
            return FirstWeek(records[^1]);
        }

        var candidates = FitEpsilonCandidates(records, completeWeeks[^1]);
        if (candidates.Length == 0)
        {
            return FirstWeek(records[^1]);
        }

        var ordered = candidates.OrderBy(item => item.Residual).ThenBy(item => item.SourceTemplate.Id).ToArray();
        var best = ordered[0];
        var unique = ordered.Length == 1;
        var fullEpsilon = best.Epsilon;

        var eight = Templates.Select(target => new PricePrediction(
            best.SourceTemplate,
            target,
            fullEpsilon,
            Predict(fullEpsilon, target, fullEpsilon))).ToArray();

        IReadOnlyList<CandidateForecast>? forecasts = null;
        if (!unique)
        {
            forecasts = ordered.Select(candidate =>
            {
                var possibilities = Templates.Select(target => new PricePrediction(
                    candidate.SourceTemplate,
                    target,
                    candidate.Epsilon,
                    Predict(candidate.Epsilon, target, candidate.Epsilon))).ToArray();
                return new CandidateForecast(
                    candidate.SourceTemplate,
                    candidate.Residual,
                    candidate.Epsilon,
                    possibilities);
            }).ToArray();
        }

        return new AnalysisResult
        {
            Stage = unique ? AnalysisStage.Locked : AnalysisStage.Pending,
            Epsilon = fullEpsilon,
            RawEpsilon = best.Epsilon,
            LockedSourceTemplate = best.SourceTemplate,
            LockedTargetTemplate = best.SourceTemplate,
            EightPredictions = eight,
            CandidateForecasts = forecasts,
            WeekCount = records.Count,
            CandidateCount = ordered.Length,
        };
    }

    private FittedEpsilonCandidate[] FitEpsilonCandidates(IReadOnlyList<WeekRecord> records, WeekRecord reference)
    {
        var scored = Templates.Select(source =>
        {
            var epsilon = ComputeEpsilon(reference.Prices, source);
            var residual = records.Sum(record => BestResidual(epsilon, record.Prices));
            return new FittedEpsilonCandidate(source, epsilon, residual);
        }).OrderBy(item => item.Residual).ToArray();
        if (scored.Length == 0)
        {
            return [];
        }

        var threshold = Math.Max(scored[0].Residual * 5, MatchThreshold);
        var filtered = scored.Where(item => item.Residual <= threshold).ToArray();
        return filtered.Length > 0 ? filtered : [scored[0]];
    }

    private double BestResidual(IReadOnlyList<double?> epsilon, IReadOnlyList<int?> actual) =>
        Templates.Min(target => MatchResidual(Predict(epsilon, target, epsilon), actual));

    private AnalysisResult FirstWeek(WeekRecord record)
    {
        var candidates = EnumerateEpsilonCandidates(record.Prices);
        return new AnalysisResult
        {
            Stage = AnalysisStage.FirstWeek,
            Candidates = candidates,
            Predictions = EnumerateAllPredictions(candidates),
            WeekCount = 1,
            CandidateCount = 8,
        };
    }

    private static int JavaScriptRound(double value) => (int)Math.Floor(value + 0.5);

    private sealed record FittedEpsilonCandidate(
        PriceTemplate SourceTemplate,
        double?[] Epsilon,
        double Residual);

    private static void ValidateWeek<T>(IReadOnlyList<T> values)
    {
        if (values.Count != 7)
        {
            throw new ArgumentException("每周必须包含周一至周日 7 个位置。");
        }
    }
}
