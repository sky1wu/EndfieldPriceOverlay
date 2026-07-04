using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PredictionStatusService(
    CaptureStore store,
    PricePredictionService prediction)
{
    public PredictionStatus Get(string itemName, DateOnly? day = null)
    {
        var today = day ?? DateOnly.FromDateTime(DateTime.Today);
        var dated = store.GetDatedPrices(itemName);
        if (dated.Count == 0)
        {
            return new(PredictionState.Insufficient, "还没有价格记录", [], []);
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
            return new(PredictionState.Insufficient, "还没有足够的价格记录", [], []);
        }

        var completeCount = weeks.Values.Count(week => week.All(value => value is not null));
        if (analysis.Stage == AnalysisStage.FirstWeek)
        {
            var message = completeCount == 0
                ? "先补齐任意一周的 7 天价格"
                : "已有完整周；下周再记录 1～2 天即可校准";
            return new(PredictionState.Insufficient, message, [], []);
        }

        if (analysis.Stage != AnalysisStage.Locked)
        {
            return new(
                PredictionState.Pending,
                $"ε 尚有 {analysis.CandidateCount} 个候选，继续记录新的价格",
                [],
                []);
        }

        var survivors = prediction.FilterCurrentWeek(analysis.EightPredictions, current)
            .Where(item => !item.Eliminated)
            .ToArray();
        var todayIndex = WeekdayIndex(today);
        var futureIndexes = Enumerable.Range(todayIndex + 1, 6 - todayIndex).ToArray();

        if (survivors.Length == 1)
        {
            var future = futureIndexes.Select(index => new FuturePrice(
                currentMonday.AddDays(index),
                index,
                survivors[0].Prices[index])).ToArray();
            return new(
                PredictionState.Ready,
                future.Length > 0 ? "本周走势已确定" : "本周已结束；下周模板将在周一重新随机",
                future,
                []);
        }

        var ranges = futureIndexes.Select(index => new FutureRange(
            currentMonday.AddDays(index),
            index,
            survivors.Min(item => item.Prices[index]),
            survivors.Max(item => item.Prices[index]))).ToArray();
        return new(
            PredictionState.Filtering,
            $"ε 已锁定，本周走势还剩 {survivors.Length} 种；再记录一天可继续排除",
            [],
            ranges);
    }

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
