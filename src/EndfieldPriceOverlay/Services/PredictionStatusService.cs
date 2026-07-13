using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PredictionStatusService(
    CaptureStore store,
    PricePredictionService prediction)
{
    public PredictionStatus Get(string itemName, DateOnly? day = null)
    {
        var today = day ?? GameCalendar.DateAt(DateTime.Now);
        var dated = store.GetDatedPrices(itemName);
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
            if (current.Any(value => value is not null)
                && analysis.CandidateForecasts is { Count: > 0 }
                && TryBuildCurrentWeekStatus(
                    analysis.CandidateForecasts.SelectMany(candidate => candidate.Possibilities),
                    currentMonday,
                    current,
                    today,
                    lockedEpsilon: false,
                    out var candidateStatus))
            {
                return candidateStatus;
            }

            return new(
                PredictionState.Pending,
                $"ε 尚有 {analysis.CandidateCount} 个候选；预计还需未来 1 天的数据，若仍未收敛再补 1 天",
                [],
                [],
                1)
            {
                CandidateTrends = DistinctTrends(
                    analysis.CandidateForecasts?.SelectMany(candidate => candidate.Possibilities) ?? []),
            };
        }

        return TryBuildCurrentWeekStatus(
            analysis.EightPredictions,
            currentMonday,
            current,
            today,
            lockedEpsilon: true,
            out var lockedStatus)
            ? lockedStatus
            : new(
                PredictionState.Pending,
                "ε 已锁定，但当前价格暂时无法匹配本周走势；请继续记录新的价格",
                [],
                [],
                1);
    }

    private bool TryBuildCurrentWeekStatus(
        IEnumerable<PricePrediction> predictions,
        DateOnly currentMonday,
        int?[] current,
        DateOnly today,
        bool lockedEpsilon,
        out PredictionStatus status)
    {
        var survivors = prediction.FilterCurrentWeek(predictions, current)
            .Where(item => !item.Eliminated)
            .GroupBy(item => string.Join(',', item.Prices))
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

        if (survivors.Length == 1)
        {
            var future = remainingIndexes.Select(index => new FuturePrice(
                currentMonday.AddDays(index),
                index,
                survivors[0].Prices[index])).ToArray();
            status = new(
                PredictionState.Ready,
                future.Length > 0 ? "本周走势已确定" : "本周已结束；下周模板将在周一重新随机",
                future,
                [],
                0);
            return true;
        }

        var ranges = futureIndexes.Select(index => new FutureRange(
            currentMonday.AddDays(index),
            index,
            survivors.Min(item => item.Prices[index]),
            survivors.Max(item => item.Prices[index]))).ToArray();
        status = new(
            PredictionState.Filtering,
            lockedEpsilon
                ? $"ε 已锁定，本周走势还剩 {survivors.Length} 种；还需未来 1 天的数据以继续排除"
                : $"本周走势还剩 {survivors.Length} 种；还需未来 1 天的数据以继续排除",
            [],
            ranges,
            1)
        {
            CandidateTrends = DistinctTrends(survivors),
        };
        return true;
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
