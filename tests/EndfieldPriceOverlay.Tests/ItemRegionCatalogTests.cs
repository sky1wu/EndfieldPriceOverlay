using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Tests;

public sealed class ItemRegionCatalogTests
{
    [Theory]
    [InlineData("锚点厨具货组")]
    [InlineData("悬空鼷兽骨雕货组")]
    [InlineData("巫术矿钻货组")]
    [InlineData("天使罐头货组")]
    [InlineData("谷地水培肉货组")]
    [InlineData("团结牌口服液货组")]
    [InlineData("塞什卡髀石货组")]
    [InlineData("源石树幼苗货组")]
    [InlineData("警戒者矿镐货组")]
    [InlineData("星体晶块货组")]
    [InlineData("边角料积木货组")]
    [InlineData("硬脑壳头盔货组")]
    public void ClassifiesValleyIvItems(string name)
    {
        Assert.Equal(ItemRegionCatalog.ValleyIv, ItemRegionCatalog.TryClassify(name));
    }

    [Theory]
    [InlineData("天师龙泡泡货组")]
    [InlineData("息壤净水芯货组")]
    [InlineData("冬虫夏笋货组")]
    [InlineData("岳研避瘴茶货组")]
    [InlineData("息壤色烟花货组")]
    [InlineData("飞天迎宾员货组")]
    [InlineData("清波筏货组")]
    [InlineData("武陵冻梨货组")]
    [InlineData("武侠电影货组")]
    public void ClassifiesWulingItems(string name)
    {
        Assert.Equal(ItemRegionCatalog.Wuling, ItemRegionCatalog.TryClassify(name));
    }

    [Fact]
    public void RemovesWhitespaceAndLeavesUnknownItemsForManualSelection()
    {
        Assert.Equal(ItemRegionCatalog.Wuling, ItemRegionCatalog.TryClassify(" 武侠电影 货组 "));
        Assert.Null(ItemRegionCatalog.TryClassify("新货物"));
    }
}
