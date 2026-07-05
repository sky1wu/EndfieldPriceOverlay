namespace EndfieldPriceOverlay.Domain;

public static class ItemRegionCatalog
{
    public const string ValleyIv = "四号谷地";
    public const string Wuling = "武陵";

    private static readonly IReadOnlyList<string> ValleyIvItems =
    [
        "锚点厨具货组",
        "悬空鼷兽骨雕货组",
        "巫术矿钻货组",
        "天使罐头货组",
        "谷地水培肉货组",
        "团结牌口服液货组",
        "塞什卡髀石货组",
        "源石树幼苗货组",
        "警戒者矿镐货组",
        "星体晶块货组",
        "边角料积木货组",
        "硬脑壳头盔货组",
    ];

    private static readonly IReadOnlyList<string> WulingItems =
    [
        "天师龙泡泡货组",
        "息壤净水芯货组",
        "冬虫夏笋货组",
        "岳研避瘴茶货组",
        "息壤色烟花货组",
        "飞天迎宾员货组",
        "清波筏货组",
        "武陵冻梨货组",
        "武侠电影货组",
    ];

    private static readonly IReadOnlyDictionary<string, string> Regions = ValleyIvItems
        .Select(name => KeyValuePair.Create(name, ValleyIv))
        .Concat(WulingItems.Select(name => KeyValuePair.Create(name, Wuling)))
        .ToDictionary(pair => pair.Key, pair => pair.Value);

    public static string? TryClassify(string itemName)
    {
        var normalized = string.Concat(itemName.Where(character => !char.IsWhiteSpace(character)));
        return Regions.GetValueOrDefault(normalized);
    }

    public static bool IsKnownRegion(string? region) => region is ValleyIv or Wuling;

    public static IReadOnlyList<string> ItemsForRegion(string region) => region switch
    {
        ValleyIv => ValleyIvItems,
        Wuling => WulingItems,
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "未知地区。"),
    };

    public static string? MatchItemName(string region, string? recognizedName)
    {
        var observed = NormalizeName(recognizedName);
        if (observed.Length < 4)
        {
            return null;
        }

        var items = ItemsForRegion(region);
        var exact = items.FirstOrDefault(item => NormalizeName(item) == observed);
        if (exact is not null)
        {
            return exact;
        }

        var contained = items
            .Where(item =>
            {
                var candidate = NormalizeName(item);
                return candidate.Contains(observed, StringComparison.Ordinal)
                    || observed.Contains(candidate, StringComparison.Ordinal);
            })
            .ToArray();
        if (contained.Length == 1)
        {
            return contained[0];
        }

        var matches = items
            .Select(item =>
            {
                var candidate = NormalizeName(item);
                var distance = EditDistance(observed, candidate);
                var similarity = 1d - distance / (double)Math.Max(observed.Length, candidate.Length);
                return (Item: item, Distance: distance, Similarity: similarity);
            })
            .OrderBy(match => match.Distance)
            .ThenByDescending(match => match.Similarity)
            .ToArray();
        var best = matches[0];
        var hasClearLead = matches.Length == 1
            || best.Similarity - matches[1].Similarity >= 0.12;
        return best.Distance <= 3 && best.Similarity >= 0.62 && hasClearLead
            ? best.Item
            : null;
    }

    public static int SortOrder(string region) => region switch
    {
        ValleyIv => 0,
        Wuling => 1,
        _ => int.MaxValue,
    };

    public static int ItemSortOrder(string region, string itemName)
    {
        var items = ItemsForRegion(region);
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index], itemName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    public static string IconPath(string itemName)
    {
        var region = TryClassify(itemName)
            ?? throw new ArgumentOutOfRangeException(nameof(itemName), itemName, "未知物资。");
        var index = ItemSortOrder(region, string.Concat(itemName.Where(character => !char.IsWhiteSpace(character))));
        var prefix = region == ValleyIv ? "valley" : "wuling";
        return $"Assets/Items/{prefix}-{index + 1:00}.png";
    }

    private static string NormalizeName(string? value) => string.Concat(
        (value ?? string.Empty).Where(character => character is >= '\u4e00' and <= '\u9fff'));

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
