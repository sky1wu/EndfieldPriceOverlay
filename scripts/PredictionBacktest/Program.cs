using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;
using Microsoft.Data.Sqlite;

// Compile the production source files directly; do not reimplement the algorithm.
// Only a read-only connection touches the input database. All mutations use scratch copies.
var input = Path.GetFullPath(args.ElementAtOrDefault(0) ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldPriceOverlay", "prices.db"));
var output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/prediction-backtest");
Directory.CreateDirectory(output);
var scratch = Path.Combine(output, "work-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var snapshot = Path.Combine(scratch, "snapshot.db");
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
using (var source = Connect(input, SqliteOpenMode.ReadOnly))
using (var destination = Connect(snapshot))
{
    source.BackupDatabase(destination);
}
var snapshotHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(snapshot)));
var truthStore = new CaptureStore(snapshot);
var truth = truthStore.GetItemNames().Order(StringComparer.Ordinal).ToDictionary(
    name => name, name => truthStore.GetDatedPrices(name));
var first = truth.Values.SelectMany(prices => prices.Keys).Min();
var last = truth.Values.SelectMany(prices => prices.Keys).Max();
var prediction = new PricePredictionService();
var runs = new List<BacktestRun>();
var points = new List<BacktestPoint>();
var diagnosticWeeks = truth.SelectMany(item => GroupWeeks(item.Value).Select(week => new
{
    Item = item.Key, Week = week.Week, Prices = week.Prices,
})).ToArray();

foreach (var mode in new[] { "price-date", "recorded-at", "previous-week" })
{
    var replayPath = Path.Combine(scratch, mode + ".db");
    var store = new CaptureStore(replayPath);
    var statusService = new PredictionStatusService(store, prediction);
    using var connection = Connect(replayPath);
    Execute(connection, "ATTACH DATABASE $path AS original", ("$path", snapshot));
    // Use the exact CaptureStore merged truth for price-date and history-window diagnostics.
    Execute(connection, "CREATE TABLE merged(item_name TEXT, price_date TEXT, price INTEGER)");
    using (var transaction = connection.BeginTransaction())
    {
        foreach (var (item, prices) in truth)
        foreach (var (date, price) in prices)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO merged VALUES ($item, $date, $price)";
            command.Parameters.AddWithValue("$item", item);
            command.Parameters.AddWithValue("$date", Format(date));
            command.Parameters.AddWithValue("$price", price);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    for (var cutoff = first; cutoff < last; cutoff = cutoff.AddDays(1))
    {
        var prefixDays = WeekdayIndex(cutoff) + 1;
        if (prefixDays == 7) continue;
        var monday = cutoff.AddDays(1 - prefixDays);
        var sunday = monday.AddDays(6);
        var boundary = cutoff.AddDays(1).ToDateTime(new TimeOnly(4, 0));
        Execute(connection, "DELETE FROM captures; DELETE FROM daily_prices; DELETE FROM price_adjustments;");
        if (mode == "recorded-at")
        {
            Execute(connection, """
                INSERT INTO captures SELECT * FROM original.captures WHERE captured_at < $boundary;
                INSERT INTO daily_prices SELECT * FROM original.daily_prices
                    WHERE captured_at < $boundary AND price_date <= $cutoff;
                INSERT INTO price_adjustments SELECT * FROM original.price_adjustments
                    WHERE updated_at < $boundary AND price_date <= $cutoff;
                """, ("$boundary", boundary.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                ("$cutoff", Format(cutoff)));
        }
        else
        {
            Execute(connection, """
                INSERT INTO daily_prices(item_name, price_date, captured_at, price, region)
                SELECT item_name, price_date, price_date || 'T12:00:00', price, '' FROM merged
                WHERE price_date <= $cutoff AND price_date >= $start;
                """, ("$cutoff", Format(cutoff)),
                ("$start", Format(mode == "previous-week" ? monday.AddDays(-7) : first)));
        }

        foreach (var (item, actual) in truth)
        {
            var targets = actual.Where(pair => pair.Key > cutoff && pair.Key <= sunday).ToArray();
            if (targets.Length == 0) continue;
            var known = store.GetDatedPrices(item);
            if (known.Keys.Any(date => date > cutoff)) throw new InvalidOperationException("Future data leaked into replay.");
            if (known.Count == 0 || !known.Keys.Any(date => date >= monday)) continue;
            var records = GroupWeeks(known);
            var current = records.Single(record => record.Week == Format(monday)).Prices;
            var analysis = prediction.AnalyzeSafe(records);
            var status = statusService.Get(item, cutoff);
            var candidatePredictions = analysis is null ? [] : analysis.Stage == AnalysisStage.Locked
                ? analysis.EightPredictions
                : analysis.CandidateForecasts?.SelectMany(candidate => candidate.Possibilities).ToArray() ?? [];
            var scored = candidatePredictions.Count > 0
                ? prediction.FilterCurrentWeek(candidatePredictions, current).ToArray() : [];
            var survivors = scored.Where(p => !p.Eliminated).ToArray();
            var lastPrice = known.Last().Value;
            var run = new BacktestRun(mode, item, Format(monday), Format(cutoff), prefixDays,
                current.Count(p => p is not null), records.Count - 1, status.State.ToString(),
                analysis?.Stage.ToString(), analysis?.CandidateCount, analysis?.LockedSourceTemplate?.Id,
                survivors.Select(p => string.Join(',', p.Prices)).Distinct().Count(),
                Finite(scored.FirstOrDefault()?.Residual),
                analysis?.Epsilon, analysis?.RawEpsilon,
                targets.Length, targets.Length == 7 - prefixDays);
            runs.Add(run);
            foreach (var (targetDate, actualPrice) in targets)
            {
                var point = status.Future.FirstOrDefault(p => p.Date == targetDate);
                var range = status.Ranges.FirstOrDefault(p => p.Date == targetDate);
                var seasonal = known.GetValueOrDefault(targetDate.AddDays(-7));
                points.Add(new BacktestPoint(mode, item, Format(monday), Format(cutoff), prefixDays,
                    Format(targetDate), targetDate.DayNumber - cutoff.DayNumber, status.State.ToString(),
                    actualPrice, point?.Price, range?.Minimum, range?.Maximum,
                    lastPrice, seasonal == 0 ? null : seasonal));
            }
        }
    }
    Console.WriteLine($"{mode}: {runs.Count(r => r.Mode == mode)} cutoffs, {points.Count(p => p.Mode == mode)} future observations");
}

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
};
var worstCases = points.Where(p => p.Mode == "recorded-at" && p.Predicted is not null)
    .GroupBy(p => p.PrefixDays == 1 ? "monday" : "later")
    .SelectMany(group => group.OrderByDescending(p => Math.Abs(p.Predicted!.Value / (double)p.Actual - 1))
        .DistinctBy(p => p.Item).Take(3))
    .Select(p => (p.Item, p.Cutoff))
    .Concat(runs.Where(r => r.Mode == "recorded-at" && r.PrefixDays == 3 && r.State == "Filtering")
        .OrderByDescending(r => r.BestResidual).DistinctBy(r => r.Item).Take(3)
        .Select(r => (r.Item, r.Cutoff)))
    .Distinct().Select(key => Diagnostics.Trace(key.Item, key.Cutoff, truth[key.Item], prediction)).ToArray();
File.WriteAllText(Path.Combine(output, "backtest.json"), JsonSerializer.Serialize(new
{
    Input = input, SnapshotSha256 = snapshotHash, FirstDate = Format(first), LastDate = Format(last),
    ItemCount = truth.Count, PriceCount = truth.Values.Sum(p => p.Count),
    Weeks = diagnosticWeeks, Runs = runs, Points = points,
}, options), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "diagnostics.json"), JsonSerializer.Serialize(new
{
    Cases = worstCases, Synthetic = Diagnostics.Synthetic(prediction),
}, options), new UTF8Encoding(false));
Console.WriteLine($"Results: {Path.Combine(output, "backtest.json")}");
// Scratch files remain with the report for auditability. The input database is never modified.

static SqliteConnection Connect(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false,
    }.ToString());
    connection.Open();
    return connection;
}

static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
    command.ExecuteNonQuery();
}

static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
static int WeekdayIndex(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;
static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
static List<WeekRecord> GroupWeeks(IReadOnlyDictionary<DateOnly, int> prices) => prices
    .GroupBy(pair => pair.Key.AddDays(-WeekdayIndex(pair.Key))).OrderBy(group => group.Key)
    .Select(group =>
    {
        var week = new int?[7];
        foreach (var (date, price) in group) week[WeekdayIndex(date)] = price;
        return new WeekRecord(Format(group.Key), week);
    }).ToList();

record BacktestRun(string Mode, string Item, string Week, string Cutoff, int PrefixDays,
    int KnownCurrentDays, int HistoryWeeks, string State, string? Stage, int? EpsilonCandidates,
    int? SourceTemplate, int SurvivingCurves, double? BestResidual, double?[]? Epsilon,
    double?[]? RawEpsilon, int TargetCount, bool CompleteFuture);
record BacktestPoint(string Mode, string Item, string Week, string Cutoff, int PrefixDays,
    string Date, int Horizon, string State, int Actual, int? Predicted, int? Minimum,
    int? Maximum, int LastPrice, int? PreviousWeekPrice);
