using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay;

public partial class PriceHistoryWindow : Window
{
    private readonly PriceRecordField[] fields;

    public PriceHistoryWindow(string itemName, IReadOnlyDictionary<DateOnly, int> prices)
    {
        ItemName = itemName;
        fields = prices
            .OrderByDescending(pair => pair.Key)
            .Select(pair => new PriceRecordField(pair.Key, pair.Value))
            .ToArray();
        InitializeComponent();
        ItemNameText.Text = itemName;
        PriceFields.ItemsSource = fields;
        UpdateSummary();
    }

    public string ItemName { get; }

    public IReadOnlyList<PriceRecordChange>? Changes { get; private set; }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PriceRecordField field })
        {
            field.IsDeleted = !field.IsDeleted;
            UpdateSummary();
        }
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var field in fields)
        {
            field.IsDeleted = true;
        }

        UpdateSummary();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (fields.Length > 0 && fields.All(field => field.IsDeleted)
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
                changes.Add(new PriceRecordChange(field.Date, null));
                continue;
            }

            if (!int.TryParse(field.Price, NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                || price is < 300 or > 5500)
            {
                MessageBox.Show(this, $"{field.DateText} 的价格必须是 300～5500 的整数。", "无法保存");
                return;
            }

            if (price != field.OriginalPrice)
            {
                changes.Add(new PriceRecordChange(field.Date, price));
            }
        }

        Changes = changes;
        DialogResult = true;
    }

    private void UpdateSummary()
    {
        var deleted = fields.Count(field => field.IsDeleted);
        SummaryText.Text = deleted == 0
            ? $"共 {fields.Length} 条记录"
            : $"共 {fields.Length} 条记录 · 将删除 {deleted} 条";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class PriceRecordField : INotifyPropertyChanged
    {
        private string price;
        private bool isDeleted;

        public PriceRecordField(DateOnly date, int originalPrice)
        {
            Date = date;
            OriginalPrice = originalPrice;
            price = originalPrice.ToString(CultureInfo.InvariantCulture);
        }

        public DateOnly Date { get; }

        public int OriginalPrice { get; }

        public string DateText => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public string WeekdayText => Date.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日",
        };

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
