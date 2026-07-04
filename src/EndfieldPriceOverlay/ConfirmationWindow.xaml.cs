using System.ComponentModel;
using System.Globalization;
using System.Windows;
using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay;

public partial class ConfirmationWindow : Window
{
    private readonly OcrReading source;
    private readonly PriceField[] fields;

    public ConfirmationWindow(OcrReading reading)
    {
        InitializeComponent();
        source = reading;
        NameBox.Text = reading.ItemName;
        fields = Enumerable.Range(0, 7).Select(index =>
        {
            var date = DateOnly.FromDateTime(reading.CapturedAt.Date.AddDays(index - 6));
            return new PriceField(
                date.ToString("MM/dd", CultureInfo.InvariantCulture),
                WeekdayName(date.DayOfWeek),
                reading.Prices[index]?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }).ToArray();
        PriceFields.ItemsSource = fields;
        HintText.Text = reading.IsConfident
            ? "识别完成，请核对商品名与 7 天价格"
            : "识别结果不完整，请补全或修正后记录";
    }

    public CaptureReading? Reading { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var prices = new int[7];
        for (var index = 0; index < fields.Length; index++)
        {
            if (!int.TryParse(fields[index].Price, NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                || price is < 300 or > 5500)
            {
                MessageBox.Show(this, $"第 {index + 1} 个价格必须是 300～5500 的整数。", "无法记录");
                return;
            }

            prices[index] = price;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "商品名称不能为空。", "无法记录");
            return;
        }

        Reading = new CaptureReading(NameBox.Text.Trim(), prices, source.CapturedAt);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };

    private sealed class PriceField(string dateText, string dayText, string initialPrice) : INotifyPropertyChanged
    {
        private string price = initialPrice;

        public string DateText { get; } = dateText;

        public string DayText { get; } = dayText;

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
