using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay;

public partial class PriceHistoryWindow : Window
{
    private readonly List<PriceRecordField> fields = [];
    private readonly ObservableCollection<PriceRecordWeek> weeks = [];

    public PriceHistoryWindow(string itemName, IReadOnlyDictionary<DateOnly, int> prices)
    {
        ItemName = itemName;
        foreach (var monday in prices.Keys.Select(MondayOf).Distinct().OrderByDescending(day => day))
        {
            weeks.Add(CreateWeek(monday, prices));
        }

        InitializeComponent();
        ItemNameText.Text = itemName;
        PriceWeeks.ItemsSource = weeks;
        UpdateSummary();
    }

    public string ItemName { get; }

    public IReadOnlyList<PriceRecordChange>? Changes { get; private set; }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PriceRecordField field } && field.HasOriginalRecord)
        {
            field.IsDeleted = !field.IsDeleted;
            UpdateSummary();
        }
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var field in fields)
        {
            field.IsDeleted = field.HasOriginalRecord;
            if (!field.HasOriginalRecord)
            {
                field.Price = string.Empty;
            }
        }

        UpdateSummary();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var existing = fields.Where(field => field.HasOriginalRecord).ToArray();
        var hasAdditions = fields.Any(field => !field.HasOriginalRecord && !string.IsNullOrWhiteSpace(field.Price));
        if (existing.Length > 0 && existing.All(field => field.IsDeleted) && !hasAdditions
            && MessageBox.Show(
                this,
                $"将删除 {ItemName} 的全部价格记录，是否继续？",
                "确认删除全部记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var changes = new List<PriceRecordChange>();
        foreach (var field in fields)
        {
            if (field.IsDeleted)
            {
                if (field.HasOriginalRecord)
                {
                    changes.Add(new PriceRecordChange(field.Date, null));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Price) && !field.HasOriginalRecord)
            {
                continue;
            }

            if (!int.TryParse(field.Price, NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                || price is < 300 or > 5500)
            {
                MessageBox.Show(this, $"{field.FullDateText} 的价格必须是 300～5500 的整数。", "无法保存");
                return;
            }

            if (field.OriginalPrice != price)
            {
                changes.Add(new PriceRecordChange(field.Date, price));
            }
        }

        Changes = changes;
        DialogResult = true;
    }

    private void UpdateSummary()
    {
        var existing = fields.Count(field => field.HasOriginalRecord);
        var additions = fields.Count(field =>
            !field.HasOriginalRecord && !string.IsNullOrWhiteSpace(field.Price));
        var deleted = fields.Count(field => field.HasOriginalRecord && field.IsDeleted);
        var parts = new List<string> { $"共 {existing} 条记录" };
        if (additions > 0)
        {
            parts.Add($"待新增 {additions} 条");
        }

        if (deleted > 0)
        {
            parts.Add($"将删除 {deleted} 条");
        }

        SummaryText.Text = string.Join(" · ", parts);
    }

    private void AddWeek_Click(object sender, RoutedEventArgs e)
    {
        var monday = MondayOf(GameCalendar.DateAt(DateTime.Now));
        while (weeks.Any(week => week.Monday == monday))
        {
            monday = monday.AddDays(-7);
        }

        var week = CreateWeek(monday, new Dictionary<DateOnly, int>());
        var index = 0;
        while (index < weeks.Count && weeks[index].Monday > monday)
        {
            index++;
        }

        weeks.Insert(index, week);
        UpdateSummary();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-((int)day.DayOfWeek + 6) % 7);

    private PriceRecordWeek CreateWeek(DateOnly monday, IReadOnlyDictionary<DateOnly, int> prices)
    {
        var days = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = monday.AddDays(offset);
                return new PriceRecordField(
                    date,
                    prices.TryGetValue(date, out var price) ? price : null);
            })
            .ToArray();
        fields.AddRange(days);
        foreach (var field in days)
        {
            field.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PriceRecordField.Price))
                {
                    UpdateSummary();
                }
            };
        }

        return new PriceRecordWeek(monday, days);
    }

    private sealed class PriceRecordWeek
    {
        public PriceRecordWeek(DateOnly monday, PriceRecordField[] days)
        {
            Monday = monday;
            DateRangeText = $"{monday:yyyy-MM-dd} — {monday.AddDays(6):MM-dd}";
            var recordCount = days.Count(day => day.HasOriginalRecord);
            RecordCountText = recordCount == 0 ? "待录入" : $"{recordCount} 条";
            Days = days;
        }

        public DateOnly Monday { get; }

        public string DateRangeText { get; }

        public string RecordCountText { get; }

        public PriceRecordField[] Days { get; }
    }

    private sealed class PriceRecordField : INotifyPropertyChanged
    {
        private string price;
        private bool isDeleted;

        public PriceRecordField(DateOnly date, int? originalPrice)
        {
            Date = date;
            OriginalPrice = originalPrice;
            price = originalPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public DateOnly Date { get; }

        public int? OriginalPrice { get; }

        public bool HasOriginalRecord => OriginalPrice is not null;

        public string DateText => Date.ToString("MM/dd", CultureInfo.InvariantCulture);

        public string FullDateText => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public string Price
        {
            get => price;
            set
            {
                price = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Price)));
            }
        }

        public bool IsDeleted
        {
            get => isDeleted;
            set
            {
                isDeleted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeleted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
            }
        }

        public string ActionText => IsDeleted ? "撤销" : "删除";

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
