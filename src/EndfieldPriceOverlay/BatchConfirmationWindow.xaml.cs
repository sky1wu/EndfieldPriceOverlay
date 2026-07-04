using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay;

public partial class BatchConfirmationWindow : Window
{
    private readonly MarketOverviewReading source;
    private BatchPriceField[] fields = [];
    private string? selectedRegion;

    public BatchConfirmationWindow(MarketOverviewReading reading)
    {
        source = reading;
        InitializeComponent();
        GameDateText.Text = GameCalendar.DateAt(reading.CapturedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (reading.Region == ItemRegionCatalog.Wuling)
        {
            WulingOption.IsChecked = true;
        }
        else
        {
            ValleyIvOption.IsChecked = true;
        }
    }

    public IReadOnlyList<DailyPriceReading>? Readings { get; private set; }

    private void Region_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string region } && ItemRegionCatalog.IsKnownRegion(region))
        {
            ShowRegion(region);
        }
    }

    private void ShowRegion(string region)
    {
        selectedRegion = region;
        var items = ItemRegionCatalog.ItemsForRegion(region);
        fields = items.Select((name, index) => new BatchPriceField(
            index + 1,
            name,
            source.Prices.ElementAtOrDefault(index),
            source.PriceConfidences.ElementAtOrDefault(index))).ToArray();
        PriceFields.ItemsSource = fields;
        var recognized = fields.Count(field => !string.IsNullOrEmpty(field.Price));
        HintText.Text = recognized == fields.Length
            ? "已识别全部价格，请核对后记录"
            : $"已识别 {recognized}/{fields.Length} 项，请补全空白价格并重点核对标记项";
        SummaryText.Text = $"{region} · {fields.Length} 项物资 · 同日再次记录会覆盖旧值";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (selectedRegion is null)
        {
            MessageBox.Show(this, "请选择地区。", "无法记录");
            return;
        }

        var readings = new List<DailyPriceReading>(fields.Length);
        foreach (var field in fields)
        {
            if (!int.TryParse(field.Price, NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                || price is < 300 or > 5500)
            {
                MessageBox.Show(this, $"{field.Name} 的价格必须是 300～5500 的整数。", "无法记录");
                return;
            }

            readings.Add(new DailyPriceReading(field.Name, price, source.CapturedAt, selectedRegion));
        }

        Readings = readings;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class BatchPriceField : INotifyPropertyChanged
    {
        private string price;

        public BatchPriceField(int number, string name, int? initialPrice, double? confidence)
        {
            Number = number.ToString("00", CultureInfo.InvariantCulture);
            Name = name;
            price = initialPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            ReviewText = initialPrice is null
                ? "未识别"
                : confidence is < 0.55
                    ? "请核对"
                    : "已识别";
        }

        public string Number { get; }

        public string Name { get; }

        public string ReviewText { get; }

        public string Price
        {
            get => price;
            set
            {
                price = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Price)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
