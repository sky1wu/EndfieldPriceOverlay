using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PricePredictionService
{
    // A rounded source price can amplify by less than 4.37x between the supplied
    // weekday templates: 0.5 * 4.37 + 0.5 < 3 price units. Never widen this gate
    // just because every available candidate fits badly.
    public const int RoundingTolerance = 3;
    public const int MinimumPrice = 300;
    public const int MaximumPrice = 5500;

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
            value = Math.Clamp(value, -0.5, 0.5);
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
        IReadOnlyList<int?> weekPrices) => FilterCurrentWeek(predictions, weekPrices)
        .Where(item => !item.Eliminated).ToArray();

    public IReadOnlyList<PricePrediction> FilterCurrentWeek(
        IEnumerable<PricePrediction> predictions,
        IReadOnlyList<int?> currentWeek)
    {
        ValidateWeek(currentWeek);
        var hasPrices = currentWeek.Any(price => price is not null);
        return predictions.Select(item =>
        {
            if (!hasPrices)
            {
                return item with { Eliminated = false, Residual = double.PositiveInfinity, Confidence = null };
            }

            var distances = Enumerable.Range(0, 7).Where(i => currentWeek[i] is not null)
                .Select(i => DistanceFromRange(currentWeek[i]!.Value, item.MinimumAt(i), item.MaximumAt(i)))
                .ToArray();
            var rejected = distances.Any(distance => distance > RoundingTolerance);
            // A fit score, not a probability; an exact rival never gives every
            // other candidate a score of one.
            var fit = 1 / (1 + distances.Average(distance => (double)distance * distance));
            return item with
            {
                Residual = MatchResidual(item.Prices, currentWeek),
                Eliminated = rejected,
                Confidence = rejected ? 0 : fit,
            };
        }).OrderBy(item => item.Residual).ToArray();
    }

    public AnalysisResult? AnalyzeSafe(IReadOnlyList<WeekRecord> records) => Analyze(records);

    public AnalysisResult? Analyze(IReadOnlyList<WeekRecord> records)
    {
        if (records.Count == 0) return null;
        foreach (var record in records) ValidateWeek(record.Prices);
        if (records.Count == 1) return FirstWeek(records[0]);

        // Partial current-week observations can eliminate hypotheses, but must
        // not be copied into their own predictions or used as historical proof.
        var current = records[^1].Prices.Any(price => price is null) ? records[^1] : null;
        var history = records.Where(record => !ReferenceEquals(record, current)
                && record.Prices.Count(price => price is not null) >= 5)
            .DistinctBy(record => string.Join(',', record.Prices)).ToArray();
        var complete = history.Where(record => record.Prices.All(price => price is not null)).ToArray();
        if (complete.Length == 0) return FirstWeek(records[^1]);

        // A hypothesis identifies a template for EACH distinct historical week.
        // Source template IDs from unrelated weeks are never intersected.
        var hypotheses = new Dictionary<string, CandidateForecast>();
        foreach (var anchor in complete)
        foreach (var source in Templates)
        {
            var seed = ComputeEpsilon(anchor.Prices, source);
            var assigned = new List<(WeekRecord Week, PriceTemplate Template)>();
            foreach (var week in history)
            {
                var match = Templates.Select(target =>
                {
                    var prices = Predict(seed, target);
                    var errors = week.Prices.Select((price, i) => price is null
                            ? (double?)null : Math.Abs(prices[i] - price.Value) / (double)price.Value)
                        .OfType<double>().Order().ToArray();
                    var inliers = week.Prices.Select((price, i) => price is not null
                        && Math.Abs(prices[i] - price.Value) <= RoundingTolerance).Count(value => value);
                    return new { Template = target, Inliers = inliers,
                        Residual = errors.Take(5).Average(error => error * error) };
                }).OrderByDescending(match => match.Inliers).ThenBy(match => match.Residual).First();
                // Five agreeing weekdays identify a historical shape while
                // allowing up to two nonlinear/extreme observations. Those
                // observations are retained in uncertainty, never discarded.
                if (match.Inliers < 5) break;
                assigned.Add((week, match.Template));
            }
            if (assigned.Count != history.Length) continue;
            var key = string.Join(',', assigned.Select(pair => pair.Template.Id));
            if (hypotheses.ContainsKey(key)) continue;

            var forecasts = BuildForecasts(source, assigned);
            if (current is not null && FilterPredictions(forecasts, current.Prices).Count == 0) continue;
            var epsilon = forecasts[0].Epsilon;
            var residual = assigned.Average(pair => MatchResidual(Predict(epsilon, pair.Template), pair.Week.Prices));
            hypotheses[key] = new CandidateForecast(source, residual, epsilon, forecasts) { SourceWeek = anchor.Week };
        }

        var candidates = hypotheses.Values.ToArray();
        var unique = candidates.Length == 1;
        var best = candidates.FirstOrDefault();
        return new AnalysisResult
        {
            Stage = unique ? AnalysisStage.Locked : AnalysisStage.Pending,
            Epsilon = best?.Epsilon,
            RawEpsilon = best is null ? null
                : ComputeEpsilon(complete.First(week => week.Week == best.SourceWeek).Prices, best.SourceTemplate),
            LockedSourceTemplate = unique ? best!.SourceTemplate : null,
            EightPredictions = unique ? best!.Possibilities : [],
            CandidateForecasts = unique ? null : candidates,
            WeekCount = records.Count,
            CandidateCount = candidates.Length,
        };
    }

    private IReadOnlyList<PricePrediction> BuildForecasts(
        PriceTemplate source,
        IReadOnlyList<(WeekRecord Week, PriceTemplate Template)> assigned)
    {
        var epsilon = new double?[7];
        var lower = new double[7];
        var upper = new double[7];
        var observedPrices = assigned.SelectMany(pair => pair.Week.Prices).OfType<int>().ToArray();
        var observedMinimum = observedPrices.Min();
        var observedMaximum = observedPrices.Max();
        for (var day = 0; day < 7; day++)
        {
            var observations = assigned.Where(pair => pair.Week.Prices[day] is not null).Select(pair =>
            {
                var price = pair.Week.Prices[day]!.Value;
                var scale = (double)pair.Template.Grid[day] * pair.Template.Coeff;
                var value = (price / (double)pair.Template.Grid[day] - 1) / pair.Template.Coeff;
                return (Value: value, Radius: 0.5 / scale);
            }).OrderBy(value => value.Value).ToArray();
            epsilon[day] = Median(observations.Select(value => value.Value));
            lower[day] = observations.Min(value => value.Value - value.Radius);
            upper[day] = observations.Max(value => value.Value + value.Radius);
        }

        return Templates.Select(target =>
        {
            var prices = Predict(epsilon, target);
            var minimum = new int[7];
            var maximum = new int[7];
            var unverified = new bool[7];
            for (var day = 0; day < 7; day++)
            {
                var observed = assigned.Where(pair => pair.Template.Id == target.Id
                        && pair.Week.Prices[day] is not null)
                    .Select(pair => pair.Week.Prices[day]!.Value).ToArray();
                if (observed.Length > 0)
                {
                    // Reuse observed same-template prices, including nonlinear
                    // extremes. No unverified global clipping rule is invented.
                    prices[day] = JavaScriptRound(Median(observed.Select(value => (double)value)));
                    minimum[day] = observed.Min() - 1;
                    maximum[day] = observed.Max() + 1;
                }
                else
                {
                    minimum[day] = (int)Math.Floor(target.Grid[day] * (1 + target.Coeff * lower[day]));
                    maximum[day] = (int)Math.Ceiling(target.Grid[day] * (1 + target.Coeff * upper[day]));
                    // A new extreme is extrapolation beyond observed support,
                    // not evidence of a particular clipping threshold. Wait
                    // for a real observation rather than publishing a bound.
                    unverified[day] = prices[day] < observedMinimum - RoundingTolerance
                        || prices[day] > observedMaximum + RoundingTolerance;
                }
            }
            return new PricePrediction(source, target, epsilon, prices)
            {
                MinimumPrices = minimum, MaximumPrices = maximum, UnverifiedDays = unverified,
            };
        }).ToArray();
    }

    public static bool IsSupportedPrice(int price) => price is >= MinimumPrice and <= MaximumPrice;

    private static int DistanceFromRange(int price, int minimum, int maximum) =>
        Math.Max(0, Math.Max(minimum - price, price - maximum));

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

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

    private static void ValidateWeek<T>(IReadOnlyList<T> values)
    {
        if (values.Count != 7)
        {
            throw new ArgumentException("每周必须包含周一至周日 7 个位置。");
        }
    }
}
