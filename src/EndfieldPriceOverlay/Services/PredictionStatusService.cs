using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PredictionStatusService(
    CaptureStore store,
    PricePredictionService prediction)
{
    public PredictionStatus Get(string itemName, DateOnly? day = null)
    {
        var today = day ?? GameCalendar.DateAt(DateTime.Now);
        var dated = store.GetDatedPrices(itemName)
            .Where(pair => pair.Key <= today)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (dated.Count == 0)
        {
            return NeedCompleteWeek(today);
        }

        var currentMonday = MondayOf(today);
        var weeks = GroupWeeks(dated);
        var history = weeks
            .Where(pair => pair.Key < currentMonday)
            .OrderBy(pair => pair.Key)
            .Select(pair => new WeekRecord(pair.Key.ToString("yyyy-MM-dd"), pair.Value))
            .ToList();
        weeks.TryGetValue(currentMonday, out var current);
        current ??= new int?[7];
        var records = new List<WeekRecord>(history);
        if (current.Any(value => value is not null))
        {
            records.Add(new WeekRecord(currentMonday.ToString("yyyy-MM-dd"), current));
        }

        var analysis = prediction.AnalyzeSafe(records);
        if (analysis is null)
        {
            return NeedCompleteWeek(today);
        }

        if (current.All(value => value is null))
        {
            return new(PredictionState.Pending, "本周尚未记录价格；记录今天价格后再校准走势", [], [], 1);
        }

        var completeCount = weeks.Values.Count(week => week.All(value => value is not null));
        if (analysis.Stage == AnalysisStage.FirstWeek)
        {
            if (completeCount == 0)
            {
                return NeedCompleteWeek(today);
            }

            var days = 7 - WeekdayIndex(today);
            return new(
                PredictionState.Insufficient,
                $"已有一周完整数据；还需未来 {days} 天的数据，到下周一再次记录即可开始跨周校准",
                [],
                [],
                days);
        }

        if (analysis.Stage != AnalysisStage.Locked)
        {
            if (analysis.CandidateCount == 0)
            {
                return Unmatched();
            }

            if (current.Any(value => value is not null)
                && analysis.CandidateForecasts is { Count: > 0 }
                && TryBuildCurrentWeekStatus(
                    analysis.CandidateForecasts.SelectMany(candidate => candidate.Possibilities),
                    currentMonday,
                    current,
                    today,
                    out var candidateStatus))
            {
                return candidateStatus;
            }

            return Unmatched();
        }

        return TryBuildCurrentWeekStatus(
            analysis.EightPredictions,
            currentMonday,
            current,
            today,
            out var lockedStatus)
            ? lockedStatus
            : Unmatched();
    }

    private bool TryBuildCurrentWeekStatus(
        IEnumerable<PricePrediction> predictions,
        DateOnly currentMonday,
        int?[] current,
        DateOnly today,
        out PredictionStatus status)
    {
        var survivors = prediction.FilterCurrentWeek(predictions, current)
            .Where(item => !item.Eliminated)
            .GroupBy(item => string.Join(',', item.Prices) + ":" + string.Join(',', item.MinimumPrices ?? [])
                + ":" + string.Join(',', item.MaximumPrices ?? []) + ":" + string.Join(',', item.UnverifiedDays ?? []))
            .Select(group => group.First())
            .ToArray();
        if (survivors.Length == 0)
        {
            status = default!;
            return false;
        }

        var todayIndex = WeekdayIndex(today);
        var remainingIndexes = Enumerable.Range(todayIndex, 7 - todayIndex).ToArray();
        var futureIndexes = remainingIndexes.Skip(1).ToArray();

        var minimum = Enumerable.Range(0, 7).Select(index => survivors.Min(item => item.MinimumAt(index))).ToArray();
        var maximum = Enumerable.Range(0, 7).Select(index => survivors.Max(item => item.MaximumAt(index))).ToArray();
        // One survivor is not sufficient: reject wide model uncertainty and
        // unsupported extrapolations, and require observations on two days.
        if (current.Count(price => price is not null) >= 2
            && remainingIndexes.Where(index => current[index] is null).All(index =>
                survivors.All(item => item.UnverifiedDays?[index] != true)
                && PricePredictionService.IsSupportedPrice(minimum[index])
                && PricePredictionService.IsSupportedPrice(maximum[index])
                && maximum[index] - minimum[index] <= 2 * PricePredictionService.RoundingTolerance))
        {
            var future = remainingIndexes.Select(index => new FuturePrice(
                currentMonday.AddDays(index),
                index,
                current[index] ?? MedianPrice(survivors.Select(item => item.Prices[index])))).ToArray();
            status = new(
                PredictionState.Ready,
                futureIndexes.Length > 0 ? "本周预测已收敛；后续实测若不匹配将重新校准" : "本周已结束；下周记录后重新校准",
                future,
                [],
                0);
            return true;
        }

        var ranges = futureIndexes.Where(index => survivors.All(item => item.UnverifiedDays?[index] != true)
                && PricePredictionService.IsSupportedPrice(minimum[index])
                && PricePredictionService.IsSupportedPrice(maximum[index]))
            .Select(index => new FutureRange(
                currentMonday.AddDays(index),
                index,
                minimum[index],
                maximum[index])).ToArray();
        status = new(
            PredictionState.Filtering,
            ranges.Length < futureIndexes.Length
                ? "存在无法可靠外推的价格；有效区间仅供参考，请继续记录"
                : "本周预测尚有歧义；区间仅供参考，请继续记录",
            [],
            ranges,
            1)
        {
            CandidateTrends = DistinctTrends(survivors),
        };
        return true;
    }

    private static PredictionStatus Unmatched() => new(
        PredictionState.Pending, "历史或当前价格与模型不匹配，已停止确定预测；请核对记录并继续采集", [], [], 1);

    private static int MedianPrice(IEnumerable<int> prices)
    {
        var ordered = prices.Order().ToArray();
        return (int)Math.Floor((ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2d + 0.5);
    }

    private static PredictionStatus NeedCompleteWeek(DateOnly today)
    {
        var days = 6 - WeekdayIndex(today);
        var message = days == 0
            ? "今天再次记录即可补齐一周完整数据"
            : $"还需未来 {days} 天的数据；记录到星期日即可形成一周完整价格";
        return new(PredictionState.Insufficient, message, [], [], days);
    }

    private static CandidateTrend[] DistinctTrends(IEnumerable<PricePrediction> predictions) => predictions
        .Where(item => item.Prices.All(PricePredictionService.IsSupportedPrice))
        .Where(item => item.UnverifiedDays?.Any(value => value) != true)
        .GroupBy(item => string.Join(',', item.Prices))
        .Select(group => new CandidateTrend(group.First().Prices.ToArray()))
        .ToArray();

    private static SortedDictionary<DateOnly, int?[]> GroupWeeks(IReadOnlyDictionary<DateOnly, int> dated)
    {
        var result = new SortedDictionary<DateOnly, int?[]>();
        foreach (var (day, price) in dated)
        {
            var monday = MondayOf(day);
            if (!result.TryGetValue(monday, out var week))
            {
                week = new int?[7];
                result[monday] = week;
            }

            week[WeekdayIndex(day)] = price;
        }

        return result;
    }

    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-WeekdayIndex(day));

    private static int WeekdayIndex(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;
}
