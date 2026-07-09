using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PurchaseRecommendationService(
    CaptureStore store,
    PredictionStatusService predictionStatus)
{
    public IReadOnlyList<RegionPurchaseRecommendation> Build(
        IReadOnlyList<RegionPurchaseSettings> settings,
        DateOnly? day = null)
    {
        var today = day ?? GameCalendar.DateAt(DateTime.Now);
        var sunday = today.AddDays(6 - WeekdayIndex(today));
        var dates = Enumerable
            .Range(0, sunday.DayNumber - today.DayNumber + 1)
            .Select(today.AddDays)
            .ToArray();
        var summaries = store.GetItemSummaries()
            .Where(item => ItemRegionCatalog.IsKnownRegion(item.Region))
            .ToArray();

        return settings.Select(setting =>
        {
            var regionItems = summaries
                .Where(item => item.Region == setting.Region)
                .OrderBy(item => ItemRegionCatalog.ItemSortOrder(setting.Region, item.Name))
                .ToArray();
            var offers = BuildDailyOffers(setting.Region, regionItems, today, sunday);
            return BuildRegionPlan(setting, offers, dates, regionItems.Length);
        }).ToArray();
    }

    public RegionPurchaseRecommendation BuildRegionPlan(
        RegionPurchaseSettings setting,
        IReadOnlyList<DailyPurchaseOffer> offers,
        IReadOnlyList<DateOnly> dates,
        int knownItemCount)
    {
        Validate(setting);
        if (setting.Limit == 0 || setting.Current == 0 && setting.DailyRecovery == 0)
        {
            return Empty(setting, "本周没有可购买数量。", isReady: true);
        }

        if (knownItemCount == 0)
        {
            return Empty(setting, "该地区还没有价格记录。", isReady: false);
        }

        var cheapestByDate = offers
            .Where(offer => offer.Region == setting.Region)
            .GroupBy(offer => offer.Date)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(offer => offer.Price)
                    .ThenBy(offer => ItemRegionCatalog.ItemSortOrder(setting.Region, offer.ItemName))
                    .ThenBy(offer => offer.ItemName, StringComparer.Ordinal)
                    .First());
        var missingDates = dates.Where(date => !cheapestByDate.ContainsKey(date)).ToArray();
        if (missingDates.Length > 0)
        {
            var missingText = string.Join('、', missingDates.Select(WeekdayName));
            return Empty(setting, $"缺少 {missingText} 的确定预测；继续记录该地区价格后再生成。", isReady: false);
        }

        var dailyOffers = dates.Select(date => cheapestByDate[date]).ToArray();
        var quantities = OptimizeQuantities(setting, dailyOffers);
        var lines = new List<PurchaseRecommendationLine>();
        var available = setting.Current;
        for (var index = 0; index < dailyOffers.Length; index++)
        {
            var quantity = quantities[index];
            var offer = dailyOffers[index];
            if (quantity > 0)
            {
                lines.Add(new PurchaseRecommendationLine(
                    setting.Region,
                    offer.Date,
                    offer.Weekday,
                    offer.ItemName,
                    offer.Price,
                    quantity,
                    available));
            }

            available -= quantity;
            if (index < dailyOffers.Length - 1)
            {
                available = Math.Min(setting.Limit, available + setting.DailyRecovery);
            }
        }

        var readyItemCount = offers
            .Where(offer => offer.Region == setting.Region)
            .Select(offer => offer.ItemName)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var catalogCount = ItemRegionCatalog.ItemsForRegion(setting.Region).Count;
        var message = readyItemCount >= catalogCount
            ? $"本周最多可买 {lines.Sum(line => line.Quantity)} 件；已按每日最低预测价分配。"
            : $"本周最多可买 {lines.Sum(line => line.Quantity)} 件；基于 {readyItemCount}/{catalogCount} 个确定预测物资分配。";
        return new RegionPurchaseRecommendation(
            setting.Region,
            setting.Current,
            setting.Limit,
            setting.DailyRecovery,
            lines.Sum(line => line.Quantity),
            lines,
            message,
            IsReady: true);
    }

    private IReadOnlyList<DailyPurchaseOffer> BuildDailyOffers(
        string region,
        IReadOnlyList<ItemSummary> items,
        DateOnly today,
        DateOnly sunday)
    {
        var offers = new List<DailyPurchaseOffer>();
        foreach (var item in items)
        {
            var status = predictionStatus.Get(item.Name, today);
            if (status.State != PredictionState.Ready)
            {
                continue;
            }

            offers.AddRange(status.Future
                .Where(day => day.Date >= today && day.Date <= sunday)
                .Select(day => new DailyPurchaseOffer(region, item.Name, day.Date, day.Weekday, day.Price)));
        }

        return offers;
    }

    private static int[] OptimizeQuantities(
        RegionPurchaseSettings setting,
        IReadOnlyList<DailyPurchaseOffer> dailyOffers)
    {
        var graph = new MinCostFlowGraph(dailyOffers.Count * 2 + 2);
        var source = dailyOffers.Count * 2;
        var sink = source + 1;
        var purchaseEdges = new int[dailyOffers.Count];
        const int infinite = int.MaxValue / 4;

        for (var index = 0; index < dailyOffers.Count; index++)
        {
            var input = index * 2;
            var output = input + 1;
            graph.AddEdge(input, output, setting.Limit, 0);
            graph.AddEdge(source, input, index == 0 ? setting.Current : setting.DailyRecovery, 0);
            purchaseEdges[index] = graph.AddEdge(output, sink, infinite, dailyOffers[index].Price);
            if (index < dailyOffers.Count - 1)
            {
                graph.AddEdge(output, input + 2, setting.Limit, 0);
            }
        }

        graph.MaxFlowMinCost(source, sink);
        return purchaseEdges.Select(edge => graph.Flow(edge)).ToArray();
    }

    private static RegionPurchaseRecommendation Empty(
        RegionPurchaseSettings setting,
        string message,
        bool isReady) => new(
            setting.Region,
            setting.Current,
            setting.Limit,
            setting.DailyRecovery,
            0,
            [],
            message,
            isReady);

    private static void Validate(RegionPurchaseSettings setting)
    {
        if (!ItemRegionCatalog.IsKnownRegion(setting.Region))
        {
            throw new ArgumentException("未知地区。", nameof(setting));
        }

        if (setting.Current < 0 || setting.Limit < 0 || setting.DailyRecovery < 0)
        {
            throw new ArgumentException("可购买数量、上限和每日恢复量不能为负数。", nameof(setting));
        }

        if (setting.Current > setting.Limit)
        {
            throw new ArgumentException($"{setting.Region} 的当前可购买数量不能超过上限。", nameof(setting));
        }
    }

    private static string WeekdayName(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };

    private static int WeekdayIndex(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;

    private sealed class MinCostFlowGraph(int nodeCount)
    {
        private readonly List<Edge> edges = [];
        private readonly List<int>[] adjacency = Enumerable.Range(0, nodeCount)
            .Select(_ => new List<int>())
            .ToArray();

        public int AddEdge(int from, int to, int capacity, int cost)
        {
            var forwardIndex = edges.Count;
            edges.Add(new Edge(from, to, capacity, cost));
            edges.Add(new Edge(to, from, 0, -cost));
            adjacency[from].Add(forwardIndex);
            adjacency[to].Add(forwardIndex + 1);
            return forwardIndex;
        }

        public void MaxFlowMinCost(int source, int sink)
        {
            var distances = new long[nodeCount];
            var previousEdge = new int[nodeCount];
            var inQueue = new bool[nodeCount];

            while (TryFindPath(source, sink, distances, previousEdge, inQueue))
            {
                var amount = int.MaxValue;
                for (var node = sink; node != source;)
                {
                    var edgeIndex = previousEdge[node];
                    amount = Math.Min(amount, edges[edgeIndex].Residual);
                    node = edges[edgeIndex].From;
                }

                for (var node = sink; node != source;)
                {
                    var edgeIndex = previousEdge[node];
                    edges[edgeIndex].Flow += amount;
                    edges[edgeIndex ^ 1].Flow -= amount;
                    node = edges[edgeIndex].From;
                }
            }
        }

        public int Flow(int edgeIndex) => edges[edgeIndex].Flow;

        private bool TryFindPath(
            int source,
            int sink,
            long[] distances,
            int[] previousEdge,
            bool[] inQueue)
        {
            Array.Fill(distances, long.MaxValue);
            Array.Fill(previousEdge, -1);
            Array.Fill(inQueue, false);
            distances[source] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(source);
            inQueue[source] = true;

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                inQueue[node] = false;
                foreach (var edgeIndex in adjacency[node])
                {
                    var edge = edges[edgeIndex];
                    if (edge.Residual <= 0 || distances[node] == long.MaxValue)
                    {
                        continue;
                    }

                    var nextDistance = distances[node] + edge.Cost;
                    if (nextDistance >= distances[edge.To])
                    {
                        continue;
                    }

                    distances[edge.To] = nextDistance;
                    previousEdge[edge.To] = edgeIndex;
                    if (!inQueue[edge.To])
                    {
                        queue.Enqueue(edge.To);
                        inQueue[edge.To] = true;
                    }
                }
            }

            return previousEdge[sink] >= 0;
        }

        private sealed class Edge(int from, int to, int capacity, int cost)
        {
            public int From { get; } = from;

            public int To { get; } = to;

            public int Capacity { get; } = capacity;

            public int Cost { get; } = cost;

            public int Flow { get; set; }

            public int Residual => Capacity - Flow;
        }
    }
}
