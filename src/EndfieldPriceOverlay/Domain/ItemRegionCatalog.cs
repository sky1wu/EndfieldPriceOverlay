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
}
