using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
        Loaded += (_, _) =>
        {
            CenterOnCurrentMonitor();
            RefreshItems();
        };
        Closing += MainWindow_Closing;
    }

    private void RefreshItems(string? selectName = null)
    {
        var rows = store.GetItemSummaries()
            .Select(item => new ItemRow(
                item.Name,
                $"{item.LatestDate:MM/dd} · {item.LatestPrice} · {item.RecordedDays} 天",
                item.Trend.Select(pair => pair.Value).ToArray(),
                item.Trend.TakeLast(7).Select(pair => new TrendDatum(pair.Key, pair.Value)).ToArray(),
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
        RecognizeButton.Content = "正在识别…";
        StatusText.Text = "正在截取 Endfield 窗口并进行离线 OCR…";
        try
        {
            var frame = await CaptureWithoutOverlayAsync();
            var reading = await Task.Run(() => ocr.Recognize(frame.Image, layoutConfig.Load()));
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
            RecognizeButton.Content = "识别当前商品";
        }
    }

    private async void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在读取 Endfield 窗口…";
        try
        {
            var frame = await CaptureWithoutOverlayAsync();
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

    private async Task<CapturedFrame> CaptureWithoutOverlayAsync()
    {
        var restoreWindow = IsVisible;
        if (restoreWindow)
        {
            Hide();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }

        try
        {
            return await Task.Run(() => capture.Capture());
        }
        finally
        {
            if (restoreWindow)
            {
                Show();
                Activate();
            }
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
        MainTrend.Values = row.Trend;
        var status = predictionStatus.Get(row.Name);
        PredictionMessageText.Text = status.Message;
        RequiredDaysBadge.Visibility = status.State != PredictionState.Ready && status.RequiredFutureDays is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        RequiredDaysText.Text = status.RequiredFutureDays == 0
            ? "今天可补齐"
            : $"还需 {status.RequiredFutureDays} 天";
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

        var reveal = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        MainTrend.BeginAnimation(OpacityProperty, reveal);
    }

    private void ShowEmptyState()
    {
        SelectedNameText.Text = "等待记录";
        SelectedMetaText.Text = "打开游戏商品详情页，然后开始识别";
        MainTrend.Values = null;
        PredictionMessageText.Text = "记录完整数据后显示预测结果";
        RequiredDaysBadge.Visibility = Visibility.Collapsed;
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

    private void DebugMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DebugMenuButton.ContextMenu is null)
        {
            return;
        }

        DebugMenuButton.ContextMenu.PlacementTarget = DebugMenuButton;
        DebugMenuButton.ContextMenu.IsOpen = true;
    }

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

    private void CenterOnCurrentMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref information))
        {
            return;
        }

        const int margin = 24;
        var workWidth = information.Work.Right - information.Work.Left;
        var workHeight = information.Work.Bottom - information.Work.Top;
        var scale = GetDpiForWindow(handle) / 96d;
        var width = Math.Min((int)Math.Round(ActualWidth * scale), workWidth - margin * 2);
        var height = Math.Min((int)Math.Round(ActualHeight * scale), workHeight - margin * 2);
        var left = information.Work.Left + (workWidth - width) / 2;
        var top = information.Work.Top + (workHeight - height) / 2;
        SetWindowPos(handle, 0, left, top, width, height, 0x0014);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed record ItemRow(string Name, string Detail, int[] Prices, TrendDatum[] Trend, ItemSummary Summary);

    private sealed record ForecastRow(string DateText, string DayText, string ValueText);
}
