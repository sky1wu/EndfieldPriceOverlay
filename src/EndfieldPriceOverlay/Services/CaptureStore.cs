using System.Globalization;
using System.IO;
using System.Text.Json;
using EndfieldPriceOverlay.Domain;
using Microsoft.Data.Sqlite;

namespace EndfieldPriceOverlay.Services;

public sealed class CaptureStore
{
    private static readonly object ProviderLock = new();
    private static bool providerConfigured;
    private readonly string connectionString;

    public CaptureStore(string? databasePath = null)
    {
        ConfigureProvider();
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EndfieldPriceOverlay",
            "prices.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
        }.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    public long Save(CaptureReading reading)
    {
        Validate(reading);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO captures(captured_at, item_name, prices_json, region)
            VALUES ($capturedAt, $itemName, $prices, $region);
            """;
        command.Parameters.AddWithValue("$capturedAt", reading.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$itemName", CleanName(reading.ItemName));
        command.Parameters.AddWithValue("$prices", JsonSerializer.Serialize(reading.Prices));
        command.Parameters.AddWithValue("$region", (object?)ResolveRegion(reading) ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("保存记录失败。");
        }

        // Windows 自带的 SQLite 可能早于支持 RETURNING 的版本。
        command.Parameters.Clear();
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(
            command.ExecuteScalar() ?? throw new InvalidOperationException("无法获取已保存记录的编号。"),
            CultureInfo.InvariantCulture);
    }

    public int SaveDailyPrices(IReadOnlyCollection<DailyPriceReading> readings)
    {
        if (readings.Count == 0)
        {
            throw new ArgumentException("没有可保存的当日价格。", nameof(readings));
        }

        foreach (var reading in readings)
        {
            Validate(reading);
        }

        if (readings.Select(reading => CleanName(reading.ItemName)).Distinct(StringComparer.Ordinal).Count() != readings.Count)
        {
            throw new ArgumentException("当日价格中存在重复物资。", nameof(readings));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var reading in readings)
        {
            var capturedAt = reading.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var priceDate = GameCalendar.DateAt(reading.CapturedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var itemName = CleanName(reading.ItemName);

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE daily_prices
                SET captured_at=$capturedAt, price=$price, region=$region
                WHERE price_date=$priceDate AND item_name=$itemName;
                """;
            update.Parameters.AddWithValue("$capturedAt", capturedAt);
            update.Parameters.AddWithValue("$price", reading.Price);
            update.Parameters.AddWithValue("$region", reading.Region);
            update.Parameters.AddWithValue("$priceDate", priceDate);
            update.Parameters.AddWithValue("$itemName", itemName);
            if (update.ExecuteNonQuery() != 0)
            {
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO daily_prices(price_date, captured_at, item_name, price, region)
                VALUES ($priceDate, $capturedAt, $itemName, $price, $region);
                """;
            insert.Parameters.AddWithValue("$priceDate", priceDate);
            insert.Parameters.AddWithValue("$capturedAt", capturedAt);
            insert.Parameters.AddWithValue("$itemName", itemName);
            insert.Parameters.AddWithValue("$price", reading.Price);
            insert.Parameters.AddWithValue("$region", reading.Region);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return readings.Count;
    }

    public int ApplyPriceChanges(string itemName, IReadOnlyCollection<PriceRecordChange> changes)
    {
        var cleanedName = CleanName(itemName);
        if (string.IsNullOrWhiteSpace(cleanedName))
        {
            throw new ArgumentException("物资名称不能为空。", nameof(itemName));
        }

        if (changes.Count == 0)
        {
            return 0;
        }

        if (changes.Select(change => change.Date).Distinct().Count() != changes.Count)
        {
            throw new ArgumentException("存在重复日期。", nameof(changes));
        }

        if (changes.Any(change => change.Price is not null and (< 300 or > 5500)))
        {
            throw new ArgumentException("价格必须是 300～5500 范围内的整数。", nameof(changes));
        }

        var updatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var change in changes)
        {
            var priceDate = change.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE price_adjustments
                SET price=$price, updated_at=$updatedAt
                WHERE item_name=$itemName AND price_date=$priceDate;
                """;
            update.Parameters.AddWithValue("$price", (object?)change.Price ?? DBNull.Value);
            update.Parameters.AddWithValue("$updatedAt", updatedAt);
            update.Parameters.AddWithValue("$itemName", cleanedName);
            update.Parameters.AddWithValue("$priceDate", priceDate);
            if (update.ExecuteNonQuery() != 0)
            {
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO price_adjustments(item_name, price_date, price, updated_at)
                VALUES ($itemName, $priceDate, $price, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$itemName", cleanedName);
            insert.Parameters.AddWithValue("$priceDate", priceDate);
            insert.Parameters.AddWithValue("$price", (object?)change.Price ?? DBNull.Value);
            insert.Parameters.AddWithValue("$updatedAt", updatedAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return changes.Count;
    }

    public void Update(long captureId, CaptureReading reading)
    {
        Validate(reading);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE captures
            SET captured_at=$capturedAt, item_name=$itemName, prices_json=$prices, region=$region
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$capturedAt", reading.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$itemName", CleanName(reading.ItemName));
        command.Parameters.AddWithValue("$prices", JsonSerializer.Serialize(reading.Prices));
        command.Parameters.AddWithValue("$region", (object?)ResolveRegion(reading) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", captureId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException("找不到需要修正的识别记录。");
        }
    }

    public IReadOnlyList<string> GetItemNames()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_name, MAX(captured_at) AS latest
            FROM (
                SELECT item_name, captured_at FROM captures
                UNION ALL
                SELECT item_name, captured_at FROM daily_prices
            )
            GROUP BY item_name
            ORDER BY latest DESC;
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public SortedDictionary<DateOnly, int> GetDatedPrices(string itemName)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, prices_json
            FROM captures
            WHERE item_name=$itemName
            ORDER BY captured_at, id;
            """;
        command.Parameters.AddWithValue("$itemName", CleanName(itemName));
        var values = new SortedDictionary<DateOnly, TimedPrice>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var capturedAt = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                var captured = GameCalendar.DateAt(capturedAt);
                var prices = JsonSerializer.Deserialize<int[]>(reader.GetString(1))
                    ?? throw new InvalidDataException("价格记录格式无效。");
                if (prices.Length != 7)
                {
                    continue;
                }

                for (var index = 0; index < 7; index++)
                {
                    SetLatest(values, captured.AddDays(index - 6), prices[index], capturedAt);
                }
            }
        }

        using var dailyCommand = connection.CreateCommand();
        dailyCommand.CommandText = """
            SELECT price_date, captured_at, price
            FROM daily_prices
            WHERE item_name=$itemName
            ORDER BY captured_at, id;
            """;
        dailyCommand.Parameters.AddWithValue("$itemName", CleanName(itemName));
        using (var dailyReader = dailyCommand.ExecuteReader())
        {
            while (dailyReader.Read())
            {
                var date = DateOnly.ParseExact(dailyReader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var capturedAt = DateTime.Parse(dailyReader.GetString(1), CultureInfo.InvariantCulture);
                SetLatest(values, date, dailyReader.GetInt32(2), capturedAt);
            }
        }

        using var adjustmentCommand = connection.CreateCommand();
        adjustmentCommand.CommandText = """
            SELECT price_date, price, updated_at
            FROM price_adjustments
            WHERE item_name=$itemName
            ORDER BY updated_at, id;
            """;
        adjustmentCommand.Parameters.AddWithValue("$itemName", CleanName(itemName));
        using var adjustmentReader = adjustmentCommand.ExecuteReader();
        while (adjustmentReader.Read())
        {
            var date = DateOnly.ParseExact(adjustmentReader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            int? price = adjustmentReader.IsDBNull(1) ? null : adjustmentReader.GetInt32(1);
            var updatedAt = DateTime.Parse(adjustmentReader.GetString(2), CultureInfo.InvariantCulture);
            SetLatest(values, date, price, updatedAt);
        }

        return new SortedDictionary<DateOnly, int>(values
            .Where(pair => pair.Value.Price is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Price!.Value));
    }

    public IReadOnlyList<ItemSummary> GetItemSummaries(int trendDays = 30)
    {
        var summaries = new List<ItemSummary>();
        foreach (var name in GetItemNames())
        {
            var dated = GetDatedPrices(name);
            if (dated.Count == 0)
            {
                continue;
            }

            var trend = dated.TakeLast(trendDays).ToArray();
            var latest = trend[^1];
            summaries.Add(new ItemSummary(name, latest.Key, latest.Value, dated.Count, trend, GetItemRegion(name)));
        }

        return summaries;
    }

    public string? GetItemRegion(string itemName)
    {
        var knownRegion = ItemRegionCatalog.TryClassify(itemName);
        if (knownRegion is not null)
        {
            return knownRegion;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT region
            FROM (
                SELECT region, captured_at, id FROM captures
                WHERE item_name=$itemName AND region IS NOT NULL
                UNION ALL
                SELECT region, captured_at, id FROM daily_prices
                WHERE item_name=$itemName AND region IS NOT NULL
            )
            ORDER BY captured_at DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$itemName", CleanName(itemName));
        return command.ExecuteScalar() as string;
    }

    private static string? ResolveRegion(CaptureReading reading) =>
        ItemRegionCatalog.TryClassify(reading.ItemName) ?? reading.Region;

    private static void SetLatest(
        IDictionary<DateOnly, TimedPrice> values,
        DateOnly date,
        int? price,
        DateTime capturedAt)
    {
        if (!values.TryGetValue(date, out var existing) || capturedAt >= existing.CapturedAt)
        {
            values[date] = new TimedPrice(price, capturedAt);
        }
    }

    private static string CleanName(string name) => string.Concat(name.Where(character => !char.IsWhiteSpace(character))).Trim();

    private static void Validate(CaptureReading reading)
    {
        if (string.IsNullOrWhiteSpace(CleanName(reading.ItemName)))
        {
            throw new ArgumentException("物资名称不能为空。");
        }

        if (reading.Prices.Length != 7 || reading.Prices.Any(price => price is < 300 or > 5500))
        {
            throw new ArgumentException("必须提供 7 个 300～5500 范围内的价格。");
        }

        if (reading.Region is not null && !ItemRegionCatalog.IsKnownRegion(reading.Region))
        {
            throw new ArgumentException("地区必须是四号谷地或武陵。");
        }
    }

    private static void Validate(DailyPriceReading reading)
    {
        if (string.IsNullOrWhiteSpace(CleanName(reading.ItemName)))
        {
            throw new ArgumentException("物资名称不能为空。");
        }

        if (reading.Price is < 300 or > 5500)
        {
            throw new ArgumentException("价格必须是 300～5500 范围内的整数。");
        }

        if (!ItemRegionCatalog.IsKnownRegion(reading.Region))
        {
            throw new ArgumentException("地区必须是四号谷地或武陵。");
        }

        if (ItemRegionCatalog.TryClassify(reading.ItemName) is { } expectedRegion
            && expectedRegion != reading.Region)
        {
            throw new ArgumentException($"{reading.ItemName} 不属于{reading.Region}。");
        }
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS captures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at TEXT NOT NULL,
                item_name TEXT NOT NULL,
                prices_json TEXT NOT NULL,
                region TEXT NULL
            );
            """;
        command.ExecuteNonQuery();

        if (!HasColumn(connection, "captures", "region"))
        {
            command.CommandText = "ALTER TABLE captures ADD COLUMN region TEXT NULL;";
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_captures_item_time
                ON captures(item_name, captured_at);
            """;
        command.ExecuteNonQuery();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS daily_prices (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                price_date TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                item_name TEXT NOT NULL,
                price INTEGER NOT NULL,
                region TEXT NOT NULL,
                UNIQUE(item_name, price_date)
            );

            CREATE INDEX IF NOT EXISTS idx_daily_prices_item_date
                ON daily_prices(item_name, price_date);
            """;
        command.ExecuteNonQuery();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS price_adjustments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                item_name TEXT NOT NULL,
                price_date TEXT NOT NULL,
                price INTEGER NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(item_name, price_date)
            );

            CREATE INDEX IF NOT EXISTS idx_price_adjustments_item_date
                ON price_adjustments(item_name, price_date);
            """;
        command.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void ConfigureProvider()
    {
        lock (ProviderLock)
        {
            if (providerConfigured)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            providerConfigured = true;
        }
    }

    private readonly record struct TimedPrice(int? Price, DateTime CapturedAt);
}
