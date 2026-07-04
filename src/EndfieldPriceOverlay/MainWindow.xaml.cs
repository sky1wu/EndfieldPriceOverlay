using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay;

public partial class MainWindow : Window
{
    private readonly CaptureStore store = new();
    private readonly PricePredictionService prediction = new();
    private readonly LayoutConfigService layoutConfig = new();
    private readonly WindowCaptureService capture = new();
    private readonly OcrService ocr;
    private readonly PredictionStatusService predictionStatus;

    public MainWindow()
    {
        InitializeComponent();
        ocr = new OcrService(layoutConfig);
        predictionStatus = new PredictionStatusService(store, prediction);
        Loaded += (_, _) => RefreshItems();
        Closing += MainWindow_Closing;
    }

    private void RefreshItems(string? selectName = null)
    {
        var rows = store.GetItemSummaries()
            .Select(item => new ItemRow(
                item.Name,
                $"{item.LatestDate:MM/dd} · {item.LatestPrice} · {item.RecordedDays} 天",
                item.Trend.Select(pair => pair.Value).ToArray(),
                item))
            .ToArray();
        ItemsList.ItemsSource = rows;
        ItemCountText.Text = $"{rows.Length} ITEMS · RECENT 30 DAYS";
        ItemsList.SelectedItem = rows.FirstOrDefault(row => row.Name == selectName) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
        {
            ShowEmptyState();
        }
    }

    private async void Recognize_Click(object sender, RoutedEventArgs e)
    {
        RecognizeButton.IsEnabled = false;
        StatusText.Text = "正在截取 Endfield 窗口并进行离线 OCR…";
        try
        {
            var reading = await Task.Run(() =>
            {
                var frame = capture.Capture();
                return ocr.Recognize(frame.Image, layoutConfig.Load());
            });
            var dialog = new ConfirmationWindow(reading) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Reading is null)
            {
                StatusText.Text = "已取消，本次识别未写入数据";
                return;
            }

            await Task.Run(() => store.Save(dialog.Reading));
            RefreshItems(dialog.Reading.ItemName);
            StatusText.Text = $"已记录 {dialog.Reading.ItemName} 的 7 天价格";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "识别失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RecognizeButton.IsEnabled = true;
        }
    }

    private async void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在读取 Endfield 窗口…";
        try
        {
            var frame = await Task.Run(() => capture.Capture());
            var dialog = new CalibrationWindow(frame.Image) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Layout is not null)
            {
                layoutConfig.Save(dialog.Layout);
                StatusText.Text = "识别区域已保存";
            }
            else
            {
                StatusText.Text = "已取消校准";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "无法校准", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ItemsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ItemRow row)
        {
            return;
        }

        SelectedNameText.Text = row.Name;
        SelectedMetaText.Text = $"最近记录 {row.Summary.LatestDate:yyyy-MM-dd} · {row.Summary.RecordedDays} 个有效日期";
        MainTrend.Values = row.Prices;
        var status = predictionStatus.Get(row.Name);
        PredictionMessageText.Text = status.Message;
        ForecastList.ItemsSource = status.Future.Count > 0
            ? status.Future.Select(value => new ForecastRow(
                value.Date.ToString("MM/dd"),
                WeekdayName(value.Weekday),
                value.Price.ToString())).ToArray()
            : status.Ranges.Select(value => new ForecastRow(
                value.Date.ToString("MM/dd"),
                WeekdayName(value.Weekday),
                value.Minimum == value.Maximum ? value.Minimum.ToString() : $"{value.Minimum} ～ {value.Maximum}"))
                .ToArray();
    }

    private void ShowEmptyState()
    {
        SelectedNameText.Text = "等待记录";
        SelectedMetaText.Text = "打开游戏商品详情页，然后开始识别";
        MainTrend.Values = null;
        PredictionMessageText.Text = "记录完整数据后显示预测结果";
        ForecastList.ItemsSource = null;
    }

    private static string WeekdayName(int index) => index switch
    {
        0 => "星期一",
        1 => "星期二",
        2 => "星期三",
        3 => "星期四",
        4 => "星期五",
        5 => "星期六",
        _ => "星期日",
    };

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshItems((ItemsList.SelectedItem as ItemRow)?.Name);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, CancelEventArgs e) => ocr.Dispose();

    private sealed record ItemRow(string Name, string Detail, int[] Prices, ItemSummary Summary);

    private sealed record ForecastRow(string DateText, string DayText, string ValueText);
}
