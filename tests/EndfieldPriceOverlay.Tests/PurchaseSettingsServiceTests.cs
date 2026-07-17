using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay.Tests;

public sealed class PurchaseSettingsServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"endfield-purchase-settings-{Guid.NewGuid():N}");

    [Fact]
    public void LoadsRegionDefaults()
    {
        var service = new PurchaseSettingsService(Path.Combine(directory, "purchase-settings.json"));

        var settings = service.Load().ToDictionary(setting => setting.Region);

        Assert.Equal(960, settings[ItemRegionCatalog.ValleyIv].Limit);
        Assert.Equal(320, settings[ItemRegionCatalog.ValleyIv].DailyRecovery);
        Assert.Equal(340, settings[ItemRegionCatalog.Wuling].Limit);
        Assert.Equal(170, settings[ItemRegionCatalog.Wuling].DailyRecovery);
    }

    [Fact]
    public void SavesAndReloadsInputs()
    {
        var path = Path.Combine(directory, "purchase-settings.json");
        var service = new PurchaseSettingsService(path);
        RegionPurchaseSettings[] saved =
        [
            new(ItemRegionCatalog.ValleyIv, Current: 320, Limit: 960, DailyRecovery: 320),
            new(ItemRegionCatalog.Wuling, Current: 170, Limit: 340, DailyRecovery: 170),
        ];

        service.Save(saved);
        var loaded = service.Load().ToDictionary(setting => setting.Region);

        Assert.Equal(saved[0], loaded[ItemRegionCatalog.ValleyIv]);
        Assert.Equal(saved[1], loaded[ItemRegionCatalog.Wuling]);
    }

    [Fact]
    public void UpdatesRecognizedRegionAndPreservesOtherRegion()
    {
        var service = new PurchaseSettingsService(Path.Combine(directory, "purchase-settings.json"));

        service.SaveRegion(new RegionPurchaseSettings(
            ItemRegionCatalog.Wuling,
            Current: 400,
            Limit: 400,
            DailyRecovery: 200));
        var loaded = service.Load().ToDictionary(setting => setting.Region);

        Assert.Equal(new RegionPurchaseSettings(ItemRegionCatalog.Wuling, 400, 400, 200), loaded[ItemRegionCatalog.Wuling]);
        Assert.Equal(new RegionPurchaseSettings(ItemRegionCatalog.ValleyIv, 0, 960, 320), loaded[ItemRegionCatalog.ValleyIv]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
