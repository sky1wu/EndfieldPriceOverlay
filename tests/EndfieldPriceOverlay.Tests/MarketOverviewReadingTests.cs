using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Tests;

public sealed class MarketOverviewReadingTests
{
    [Fact]
    public void MatchesPricesByOcrItemNameInsteadOfSlotOrder()
    {
        var reading = new MarketOverviewReading(
            ItemRegionCatalog.Wuling,
            [
                new("武侠电影货组", 3502, 0.98, 0.99),
                new("息壤净水芯货组", 3138, 0.98, 0.99),
                new("岳研避瘴茶货组", 1776, 0.98, 0.99),
                new("飞天迎宾员货组", 1600, 0.98, 0.99),
                new("冬虫夏笋货组", 1451, 0.98, 0.99),
                new("息壤色烟花货组", 1372, 0.98, 0.99),
                new("天师龙泡泡货组", 1328, 0.98, 0.99),
                new("武陵冻梨货组", 970, 0.98, 0.99),
                new("清波筏货组", 924, 0.98, 0.99),
            ],
            new DateTime(2026, 7, 5));

        var matched = reading.MatchItems(ItemRegionCatalog.Wuling);

        Assert.Equal(3502, matched["武侠电影货组"].Price);
        Assert.Equal(1328, matched["天师龙泡泡货组"].Price);
        Assert.Equal(924, matched["清波筏货组"].Price);
        Assert.Equal([
            "武侠电影货组",
            "息壤净水芯货组",
            "岳研避瘴茶货组",
            "飞天迎宾员货组",
            "冬虫夏笋货组",
            "息壤色烟花货组",
            "天师龙泡泡货组",
            "武陵冻梨货组",
            "清波筏货组",
        ], reading.ItemNamesInGameOrder(ItemRegionCatalog.Wuling));
    }

    [Fact]
    public void DoesNotBindPriceWhenItemNameCannotBeMatched()
    {
        var reading = new MarketOverviewReading(
            ItemRegionCatalog.Wuling,
            [new("未识别商品", 3502, 0.9, 0.9)],
            new DateTime(2026, 7, 5));

        Assert.Empty(reading.MatchItems(ItemRegionCatalog.Wuling));
    }
}
