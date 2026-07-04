namespace EndfieldPriceOverlay.Domain;

public sealed record PriceTemplate(int Id, string Name, int[] Grid, int Coeff);

public sealed record WeekRecord(string Week, int?[] Prices);

public sealed record EpsilonCandidate(PriceTemplate SourceTemplate, double?[] Epsilon);

public sealed record PricePrediction(
    PriceTemplate SourceTemplate,
    PriceTemplate TargetTemplate,
    double?[] Epsilon,
    int[] Prices,
    double Residual = double.PositiveInfinity,
    bool Eliminated = false,
    double? Confidence = null);

public sealed record CandidateForecast(
    PriceTemplate SourceTemplate,
    double Residual,
    double?[] Epsilon,
    IReadOnlyList<PricePrediction> Possibilities);

public enum AnalysisStage
{
    FirstWeek,
    Pending,
    Locked,
}

public sealed record AnalysisResult
{
    public required AnalysisStage Stage { get; init; }

    public double?[]? Epsilon { get; init; }

    public double?[]? RawEpsilon { get; init; }

    public PriceTemplate? LockedSourceTemplate { get; init; }

    public PriceTemplate? LockedTargetTemplate { get; init; }

    public IReadOnlyList<EpsilonCandidate> Candidates { get; init; } = [];

    public IReadOnlyList<PricePrediction> Predictions { get; init; } = [];

    public IReadOnlyList<PricePrediction> EightPredictions { get; init; } = [];

    public IReadOnlyList<CandidateForecast>? CandidateForecasts { get; init; }

    public int WeekCount { get; init; }

    public int CandidateCount { get; init; } = 8;
}

public sealed record CaptureReading(
    string ItemName,
    int[] Prices,
    DateTime CapturedAt,
    double NameConfidence = 1,
    IReadOnlyList<double>? PriceConfidences = null);

public sealed record ItemSummary(
    string Name,
    DateOnly LatestDate,
    int LatestPrice,
    int RecordedDays,
    IReadOnlyList<KeyValuePair<DateOnly, int>> Trend);

public enum PredictionState
{
    Insufficient,
    Pending,
    Filtering,
    Ready,
}

public sealed record FuturePrice(DateOnly Date, int Weekday, int Price);

public sealed record FutureRange(DateOnly Date, int Weekday, int Minimum, int Maximum);

public sealed record PredictionStatus(
    PredictionState State,
    string Message,
    IReadOnlyList<FuturePrice> Future,
    IReadOnlyList<FutureRange> Ranges);
