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
            VALUES ($capturedAt, $itemName, $prices, $region)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$capturedAt", reading.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$itemName", CleanName(reading.ItemName));
        command.Parameters.AddWithValue("$prices", JsonSerializer.Serialize(reading.Prices));
        command.Parameters.AddWithValue("$region", (object?)ResolveRegion(reading) ?? DBNull.Value);
        return (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("保存记录失败。"));
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
            FROM captures
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
        using var reader = command.ExecuteReader();
        var values = new SortedDictionary<DateOnly, int>();
        while (reader.Read())
        {
            var captured = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture).Date;
            var prices = JsonSerializer.Deserialize<int[]>(reader.GetString(1))
                ?? throw new InvalidDataException("价格记录格式无效。");
            if (prices.Length != 7)
            {
                continue;
            }

            for (var index = 0; index < 7; index++)
            {
                values[DateOnly.FromDateTime(captured.AddDays(index - 6))] = prices[index];
            }
        }

        return values;
    }

    public IReadOnlyList<ItemSummary> GetItemSummaries(int trendDays = 30) =>
        GetItemNames().Select(name =>
        {
            var dated = GetDatedPrices(name);
            var trend = dated.TakeLast(trendDays).ToArray();
            var latest = trend[^1];
            return new ItemSummary(name, latest.Key, latest.Value, dated.Count, trend, GetItemRegion(name));
        }).ToArray();

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
            FROM captures
            WHERE item_name=$itemName AND region IS NOT NULL
            ORDER BY captured_at DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$itemName", CleanName(itemName));
        return command.ExecuteScalar() as string;
    }

    private static string? ResolveRegion(CaptureReading reading) =>
        ItemRegionCatalog.TryClassify(reading.ItemName) ?? reading.Region;

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
}
