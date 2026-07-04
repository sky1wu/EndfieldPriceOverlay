using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;
using Microsoft.Data.Sqlite;
using System.IO;

namespace EndfieldPriceOverlay.Tests;

public sealed class CaptureStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"endfield-{Guid.NewGuid():N}");

    [Fact]
    public void ExistingPythonSchemaMapsOldestPriceToSixDaysAgo()
    {
        var store = new CaptureStore(Path.Combine(directory, "prices.db"));
        store.Save(new CaptureReading(
            "天师龙泡泡货组",
            [1400, 2605, 2343, 2755, 1360, 1218, 3416],
            new DateTime(2026, 7, 4, 12, 0, 0)));

        var values = store.GetDatedPrices("天师龙泡泡货组");

        Assert.Equal(1400, values[new DateOnly(2026, 6, 28)]);
        Assert.Equal(3416, values[new DateOnly(2026, 7, 4)]);
    }

    [Fact]
    public void CaptureBeforeFourAmBelongsToPreviousGameDate()
    {
        var store = new CaptureStore(Path.Combine(directory, "before-reset.db"));
        store.Save(new CaptureReading(
            "商品",
            [1000, 1100, 1200, 1300, 1400, 1500, 1600],
            new DateTime(2026, 7, 4, 3, 59, 59)));

        var values = store.GetDatedPrices("商品");

        Assert.Equal(1600, values[new DateOnly(2026, 7, 3)]);
        Assert.DoesNotContain(new DateOnly(2026, 7, 4), values.Keys);
    }

    [Fact]
    public void LatestCaptureWinsForSameDate()
    {
        var store = new CaptureStore(Path.Combine(directory, "prices.db"));
        store.Save(new CaptureReading("商品", [1000, 1100, 1200, 1300, 1400, 1500, 1600], new DateTime(2026, 7, 4, 12, 0, 0)));
        store.Save(new CaptureReading("商品", [1001, 1101, 1201, 1301, 1401, 1501, 1601], new DateTime(2026, 7, 4, 13, 0, 0)));

        Assert.Equal(1601, store.GetDatedPrices("商品")[new DateOnly(2026, 7, 4)]);
    }

    [Fact]
    public void SaveReturnsIdThatCanBeUpdated()
    {
        var store = new CaptureStore(Path.Combine(directory, "update-by-id.db"));
        var id = store.Save(new CaptureReading(
            "商品",
            [1000, 1100, 1200, 1300, 1400, 1500, 1600],
            new DateTime(2026, 7, 4, 12, 0, 0)));

        store.Update(id, new CaptureReading(
            "商品",
            [1001, 1101, 1201, 1301, 1401, 1501, 1601],
            new DateTime(2026, 7, 4, 13, 0, 0)));

        Assert.True(id > 0);
        Assert.Equal(1601, store.GetDatedPrices("商品")[new DateOnly(2026, 7, 4)]);
    }

    [Fact]
    public void ManualRegionIsStoredForUnknownItemName()
    {
        var store = new CaptureStore(Path.Combine(directory, "manual-region.db"));
        store.Save(new CaptureReading(
            "新货物",
            [1000, 1100, 1200, 1300, 1400, 1500, 1600],
            new DateTime(2026, 7, 4, 12, 0, 0),
            Region: ItemRegionCatalog.Wuling));

        Assert.Equal(ItemRegionCatalog.Wuling, store.GetItemSummaries().Single().Region);
        Assert.Equal(ItemRegionCatalog.Wuling, store.GetItemRegion("新货物"));
    }

    [Fact]
    public void DailyPricesCreateItemsAndReplaceSameGameDate()
    {
        var store = new CaptureStore(Path.Combine(directory, "daily-prices.db"));
        var initial = new DailyPriceReading(
            "锚点厨具货组",
            3004,
            new DateTime(2026, 7, 4, 12, 0, 0),
            ItemRegionCatalog.ValleyIv);
        store.SaveDailyPrices([initial]);

        store.SaveDailyPrices([initial with { Price = 3100, CapturedAt = new DateTime(2026, 7, 4, 13, 0, 0) }]);

        var summary = Assert.Single(store.GetItemSummaries());
        Assert.Equal("锚点厨具货组", summary.Name);
        Assert.Equal(new DateOnly(2026, 7, 4), summary.LatestDate);
        Assert.Equal(3100, summary.LatestPrice);
        Assert.Equal(ItemRegionCatalog.ValleyIv, summary.Region);
    }

    [Fact]
    public void NewerDetailCaptureCanReplaceBatchPrice()
    {
        var store = new CaptureStore(Path.Combine(directory, "merged-prices.db"));
        store.SaveDailyPrices([
            new DailyPriceReading(
                "锚点厨具货组",
                3004,
                new DateTime(2026, 7, 4, 12, 0, 0),
                ItemRegionCatalog.ValleyIv),
        ]);
        store.Save(new CaptureReading(
            "锚点厨具货组",
            [1000, 1100, 1200, 1300, 1400, 1500, 3200],
            new DateTime(2026, 7, 4, 13, 0, 0)));

        Assert.Equal(3200, store.GetDatedPrices("锚点厨具货组")[new DateOnly(2026, 7, 4)]);
    }

    [Fact]
    public void ExistingDatabaseIsMigratedBeforeRegionIsStored()
    {
        _ = new CaptureStore(Path.Combine(directory, "provider-bootstrap.db"));
        var databasePath = Path.Combine(directory, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE captures (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    captured_at TEXT NOT NULL,
                    item_name TEXT NOT NULL,
                    prices_json TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var store = new CaptureStore(databasePath);
        store.Save(new CaptureReading(
            "新货物",
            [1000, 1100, 1200, 1300, 1400, 1500, 1600],
            new DateTime(2026, 7, 4, 12, 0, 0),
            Region: ItemRegionCatalog.ValleyIv));

        Assert.Equal(ItemRegionCatalog.ValleyIv, store.GetItemSummaries().Single().Region);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
