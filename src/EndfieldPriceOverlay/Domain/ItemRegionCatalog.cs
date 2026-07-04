namespace EndfieldPriceOverlay.Domain;

public static class ItemRegionCatalog
{
    public const string ValleyIv = "四号谷地";
    public const string Wuling = "武陵";

    private static readonly IReadOnlyDictionary<string, string> Regions = new Dictionary<string, string>
    {
        ["锚点厨具货组"] = ValleyIv,
        ["悬空鼷兽骨雕货组"] = ValleyIv,
        ["巫术矿钻货组"] = ValleyIv,
        ["天使罐头货组"] = ValleyIv,
        ["谷地水培肉货组"] = ValleyIv,
        ["团结牌口服液货组"] = ValleyIv,
        ["塞什卡髀石货组"] = ValleyIv,
        ["源石树幼苗货组"] = ValleyIv,
        ["警戒者矿镐货组"] = ValleyIv,
        ["星体晶块货组"] = ValleyIv,
        ["边角料积木货组"] = ValleyIv,
        ["硬脑壳头盔货组"] = ValleyIv,
        ["天师龙泡泡货组"] = Wuling,
        ["息壤净水芯货组"] = Wuling,
        ["冬虫夏笋货组"] = Wuling,
        ["岳研避瘴茶货组"] = Wuling,
        ["息壤色烟花货组"] = Wuling,
        ["飞天迎宾员货组"] = Wuling,
        ["清波筏货组"] = Wuling,
        ["武陵冻梨货组"] = Wuling,
        ["武侠电影货组"] = Wuling,
    };

    public static string? TryClassify(string itemName)
    {
        var normalized = string.Concat(itemName.Where(character => !char.IsWhiteSpace(character)));
        return Regions.GetValueOrDefault(normalized);
    }

    public static bool IsKnownRegion(string? region) => region is ValleyIv or Wuling;

    public static int SortOrder(string region) => region switch
    {
        ValleyIv => 0,
        Wuling => 1,
        _ => int.MaxValue,
    };
}
