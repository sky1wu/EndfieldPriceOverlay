using System.ComponentModel;
using System.Globalization;
using System.Windows;
using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay;

public partial class ConfirmationWindow : Window
{
    private readonly OcrReading source;
    private readonly PriceField[] fields;
    private readonly string initialItemName;
    private readonly string? previousRegion;

    public ConfirmationWindow(OcrReading reading, string? previousRegion = null)
    {
        source = reading;
        initialItemName = NormalizeName(reading.ItemName);
        this.previousRegion = previousRegion;
        InitializeComponent();
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
            ? "识别完成，请核对物资名称与 7 天价格"
            : "识别结果不完整，请补全或修正后记录";
        UpdateRegionFromName();
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
            MessageBox.Show(this, "物资名称不能为空。", "无法记录");
            return;
        }

        var region = SelectedRegion();
        if (region is null)
        {
            MessageBox.Show(this, "无法自动判断地区，请选择四号谷地或武陵。", "无法记录");
            return;
        }

        Reading = new CaptureReading(NameBox.Text.Trim(), prices, source.CapturedAt, Region: region);
        DialogResult = true;
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateRegionFromName();

    private void UpdateRegionFromName()
    {
        if (RegionChoices is null)
        {
            return;
        }

        var automaticRegion = ItemRegionCatalog.TryClassify(NameBox.Text);
        var savedRegion = automaticRegion is null && NormalizeName(NameBox.Text) == initialItemName
            ? previousRegion
            : null;
        var selectedRegion = automaticRegion ?? savedRegion;
        RegionChoices.IsEnabled = automaticRegion is null;
        ValleyIvOption.IsChecked = selectedRegion == ItemRegionCatalog.ValleyIv;
        WulingOption.IsChecked = selectedRegion == ItemRegionCatalog.Wuling;
        RegionHintText.Text = automaticRegion is not null
            ? "已自动归类"
            : savedRegion is not null
                ? "已沿用上次选择"
                : "请选择地区";
        RegionHintText.Foreground = selectedRegion is null
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD5, 0xA8, 0x36))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x84, 0x7A));
    }

    private string? SelectedRegion() =>
        ValleyIvOption.IsChecked == true
            ? ItemRegionCatalog.ValleyIv
            : WulingOption.IsChecked == true
                ? ItemRegionCatalog.Wuling
                : null;

    private static string NormalizeName(string name) =>
        string.Concat(name.Where(character => !char.IsWhiteSpace(character)));

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
