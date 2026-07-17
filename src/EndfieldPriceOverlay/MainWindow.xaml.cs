using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly PurchaseSettingsService purchaseSettings = new();
    private readonly WindowCaptureService capture = new();
    private readonly Dictionary<string, bool> regionExpansion = new(StringComparer.Ordinal);
    private readonly OcrService ocr;
    private readonly PredictionStatusService predictionStatus;
    private Point dragStartPoint;
    private ItemRow? draggedItem;
    private ListBoxItem? dropTargetContainer;
    private bool dropAfterTarget;
    private string? stickyRegion;

    public MainWindow()
    {
        InitializeComponent();
        ocr = new OcrService(layoutConfig);
        predictionStatus = new PredictionStatusService(store, prediction);
        Loaded += (_, _) =>
        {
            CenterOnCurrentMonitor();
            RefreshItems();
            AnimateWindowEntrance();
        };
    }

    private void RefreshItems(string? selectName = null)
    {
        var customOrders = store.GetItemSortOrders();
        var rows = store.GetItemSummaries()
            .Where(item => ItemRegionCatalog.IsKnownRegion(item.Region))
            .Select(item => new ItemRow(
                item.Name,
                ItemRegionCatalog.IconPath(item.Name),
                item.Region!,
                $"{item.LatestDate:MM/dd} · {item.LatestPrice} · {item.RecordedDays} 天",
                item.Trend.Select(pair => pair.Value).ToArray(),
                item.Trend.TakeLast(7).Select(pair => new TrendDatum(pair.Key, pair.Value)).ToArray(),
                item))
            .OrderBy(row => ItemRegionCatalog.SortOrder(row.Region))
            .ThenBy(row => customOrders.TryGetValue(row.Name, out var order) ? order : int.MaxValue)
            .ThenByDescending(row => row.Summary.LatestDate)
            .ThenBy(row => ItemRegionCatalog.ItemSortOrder(row.Region, row.Name))
            .ToArray();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemRow.Region)));
        ItemsList.ItemsSource = view;
        ItemCountText.Text = $"{rows.Length} GOODS · RECENT 30 DAYS";
        ItemsList.SelectedItem = rows.FirstOrDefault(row => row.Name == selectName) ?? rows.FirstOrDefault();
        _ = Dispatcher.InvokeAsync(UpdateStickyRegion, DispatcherPriority.Loaded);
        if (rows.Length == 0)
        {
            ShowEmptyState();
        }
    }

    private async void Recognize_Click(object sender, RoutedEventArgs e)
    {
        SetRecognitionBusy(RecognizeButton, "正在识别…");
        StatusText.Text = "正在截取 Endfield 窗口并进行离线 OCR…";
        try
        {
            var frame = await CaptureWithoutOverlayAsync();
            var reading = await Task.Run(() => ocr.Recognize(frame.Image, layoutConfig.Load()));
            var dialog = new ConfirmationWindow(reading, store.GetItemRegion(reading.ItemName)) { Owner = this };
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
            ClearRecognitionBusy();
        }
    }

    private async void BatchRecognize_Click(object sender, RoutedEventArgs e)
    {
        SetRecognitionBusy(BatchRecognizeButton, "正在批量识别…");
        StatusText.Text = "正在识别地区调度页的全部今日价格…";
        try
        {
            var frame = await CaptureWithoutOverlayAsync();
            var reading = await Task.Run(() => ocr.RecognizeMarketOverview(frame.Image));
            var dialog = new BatchConfirmationWindow(reading) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Readings is null || dialog.PurchaseSettings is null)
            {
                StatusText.Text = "已取消，本次批量识别未写入数据";
                return;
            }

            var count = await Task.Run(() =>
            {
                var saved = store.SaveDailyPrices(dialog.Readings);
                purchaseSettings.SaveRegion(dialog.PurchaseSettings);
                return saved;
            });
            RefreshItems(dialog.Readings[0].ItemName);
            StatusText.Text = $"已记录 {dialog.Readings[0].Region} {count} 项今日价格与购买额度";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "批量识别失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ClearRecognitionBusy();
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

        HideCandidateTrends();
        SelectedNameText.Text = row.Name;
        EditPricesButton.IsEnabled = true;
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
        var weekPrices = BuildWeekPrices(row.Name, status);
        var hasWeekPrices = weekPrices.Any(value => value.Low is not null);
        ForecastChart.Values = weekPrices;
        ForecastChart.Visibility = hasWeekPrices ? Visibility.Visible : Visibility.Collapsed;
        ForecastEmptyText.Visibility = hasWeekPrices ? Visibility.Collapsed : Visibility.Visible;
        UpdateCandidateTrends(status);

        var reveal = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        MainTrend.BeginAnimation(OpacityProperty, reveal);
        AnimateSelectionHeader();
    }

    private void SetRecognitionBusy(Button activeButton, string content)
    {
        RecognizeButton.IsEnabled = false;
        BatchRecognizeButton.IsEnabled = false;
        activeButton.Tag = "Busy";
        activeButton.Content = content;
        StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void ClearRecognitionBusy()
    {
        RecognizeButton.Tag = null;
        BatchRecognizeButton.Tag = null;
        RecognizeButton.IsEnabled = true;
        BatchRecognizeButton.IsEnabled = true;
        RecognizeButton.Content = "识别当前物资";
        BatchRecognizeButton.Content = "批量识别今日价格";
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    private void AnimateWindowEntrance()
    {
        AnimateEntrance(ItemsPanel, -14, 0);
        AnimateEntrance(RightContentGrid, 18, 55);
        AnimateEntrance(StatusBar, 0, 120);
    }

    private static void AnimateEntrance(FrameworkElement element, double offsetX, int delayMilliseconds)
    {
        var transform = new TranslateTransform(offsetX, 0);
        element.RenderTransform = transform;
        element.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(260);
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation(0, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = easing,
        };
        var movement = new DoubleAnimation(offsetX, 0, duration)
        {
            BeginTime = delay,
            EasingFunction = easing,
        };
        opacity.Completed += (_, _) =>
        {
            element.Opacity = 1;
            element.BeginAnimation(OpacityProperty, null);
            transform.X = 0;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
        };
        element.BeginAnimation(OpacityProperty, opacity);
        transform.BeginAnimation(TranslateTransform.XProperty, movement);
    }

    private void AnimateSelectionHeader()
    {
        var transform = SelectedHeader.RenderTransform as TranslateTransform ?? new TranslateTransform();
        SelectedHeader.RenderTransform = transform;
        SelectedHeader.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void UpdateCandidateTrends(PredictionStatus status)
    {
        var candidates = status.CandidateTrends;
        CandidateTrendsButton.Visibility = status.State != PredictionState.Ready && candidates.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        CandidateTrendsButton.Content = $"展开全部 {candidates.Count} 种走势";

        var today = GameCalendar.DateAt(DateTime.Now);
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        CandidateTrendsTitle.Text = $"全部候选走势 · {candidates.Count}";
        CandidateWeekText.Text = $"相同价格序列已合并 · {monday:yyyy-MM-dd} — {monday.AddDays(6):MM-dd}";
        CandidateTrendRows.ItemsSource = candidates
            .Select((candidate, index) => new CandidateTrendRow(
                $"{index + 1:00}",
                candidate.Prices.ToArray(),
                candidate.Prices
                    .Select((price, day) => new TrendDatum(monday.AddDays(day), price))
                    .ToArray()))
            .ToArray();
    }

    private void CandidateTrends_Click(object sender, RoutedEventArgs e)
    {
        CandidateTrendsPanel.Visibility = Visibility.Visible;
        var reveal = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        CandidateTrendsPanel.BeginAnimation(OpacityProperty, reveal);
    }

    private void CloseCandidateTrends_Click(object sender, RoutedEventArgs e) => HideCandidateTrends();

    private void HideCandidateTrends()
    {
        CandidateTrendsPanel.BeginAnimation(OpacityProperty, null);
        CandidateTrendsPanel.Visibility = Visibility.Collapsed;
    }

    private WeekPriceDatum[] BuildWeekPrices(string itemName, PredictionStatus status)
    {
        var today = GameCalendar.DateAt(DateTime.Now);
        var monday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var actual = store.GetDatedPrices(itemName)
            .Where(pair => pair.Key >= monday && pair.Key <= monday.AddDays(6))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var future = status.Future.ToDictionary(value => value.Date);
        var ranges = status.Ranges.ToDictionary(value => value.Date);
        var result = new List<WeekPriceDatum>(7);

        for (var index = 0; index < 7; index++)
        {
            var date = monday.AddDays(index);
            if (actual.TryGetValue(date, out var price))
            {
                result.Add(new WeekPriceDatum(date, price));
            }
            else if (future.TryGetValue(date, out var prediction))
            {
                result.Add(new WeekPriceDatum(date, prediction.Price));
            }
            else if (ranges.TryGetValue(date, out var range))
            {
                result.Add(new WeekPriceDatum(date, null, range.Minimum, range.Maximum));
            }
            else
            {
                result.Add(new WeekPriceDatum(date, null));
            }
        }

        return result.ToArray();
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStartPoint = e.GetPosition(ItemsList);
        draggedItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ItemRow;
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedItem is null)
        {
            return;
        }

        var position = e.GetPosition(ItemsList);
        if (Math.Abs(position.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = draggedItem;
        try
        {
            DragDrop.DoDragDrop(ItemsList, new DataObject(typeof(ItemRow), item), DragDropEffects.Move);
        }
        finally
        {
            draggedItem = null;
            ClearDropMarker();
        }
    }

    private void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ItemRow)) is not ItemRow source)
        {
            RejectDrop(e);
            return;
        }

        var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is not ItemRow target
            || target.Region != source.Region
            || target.Name == source.Name)
        {
            RejectDrop(e);
            return;
        }

        var pointer = e.GetPosition(targetContainer);
        SetDropMarker(targetContainer, pointer.Y >= targetContainer.ActualHeight / 2);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var listPointer = e.GetPosition(ItemsList);
        var scrollViewer = VisualDescendants<ScrollViewer>(ItemsList).FirstOrDefault();
        if (listPointer.Y < 24)
        {
            scrollViewer?.LineUp();
        }
        else if (listPointer.Y > ItemsList.ActualHeight - 24)
        {
            scrollViewer?.LineDown();
        }
    }

    private void ItemsList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(typeof(ItemRow)) is not ItemRow source
                || dropTargetContainer?.DataContext is not ItemRow target
                || source.Region != target.Region)
            {
                return;
            }

            var ordered = ItemsList.Items.Cast<ItemRow>()
                .Where(row => row.Region == source.Region && row.Name != source.Name)
                .ToList();
            var targetIndex = ordered.FindIndex(row => row.Name == target.Name);
            if (targetIndex < 0)
            {
                return;
            }

            ordered.Insert(targetIndex + (dropAfterTarget ? 1 : 0), source);
            store.SaveItemOrder(ordered.Select(row => row.Name).ToArray());
            RefreshItems(source.Name);
            StatusText.Text = $"已调整 {source.Region}物资顺序";
            e.Handled = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "无法调整物资顺序", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ClearDropMarker();
        }
    }

    private void RejectDrop(DragEventArgs e)
    {
        ClearDropMarker();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void SetDropMarker(ListBoxItem container, bool after)
    {
        if (dropTargetContainer != container || dropAfterTarget != after)
        {
            ClearDropMarker();
        }

        dropTargetContainer = container;
        dropAfterTarget = after;
        var markerName = after ? "DropMarkerBottom" : "DropMarkerTop";
        if (container.Template.FindName(markerName, container) is Border marker)
        {
            marker.Visibility = Visibility.Visible;
        }
    }

    private void ClearDropMarker()
    {
        if (dropTargetContainer is not null)
        {
            if (dropTargetContainer.Template.FindName("DropMarkerTop", dropTargetContainer) is Border top)
            {
                top.Visibility = Visibility.Collapsed;
            }

            if (dropTargetContainer.Template.FindName("DropMarkerBottom", dropTargetContainer) is Border bottom)
            {
                bottom.Visibility = Visibility.Collapsed;
            }
        }

        dropTargetContainer = null;
    }

    private void RegionExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: string region } expander)
        {
            return;
        }

        expander.IsExpanded = !regionExpansion.TryGetValue(region, out var expanded) || expanded;
        _ = Dispatcher.InvokeAsync(UpdateStickyRegion, DispatcherPriority.Loaded);
    }

    private void RegionExpander_Expanded(object sender, RoutedEventArgs e) => SetRegionExpansion(sender, true);

    private void RegionExpander_Collapsed(object sender, RoutedEventArgs e) => SetRegionExpansion(sender, false);

    private void SetRegionExpansion(object sender, bool expanded)
    {
        if (sender is Expander { Tag: string region })
        {
            regionExpansion[region] = expanded;
            _ = Dispatcher.InvokeAsync(UpdateStickyRegion, DispatcherPriority.Loaded);
        }
    }

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateStickyRegion();

    private void ItemsListHost_SizeChanged(object sender, SizeChangedEventArgs e) => AlignStickyRegionHeader();

    private void StickyRegionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (stickyRegion is null)
        {
            return;
        }

        var expanded = StickyRegionToggle.IsChecked == true;
        regionExpansion[stickyRegion] = expanded;
        var expander = VisualDescendants<Expander>(ItemsList)
            .FirstOrDefault(item => string.Equals(item.Tag as string, stickyRegion, StringComparison.Ordinal));
        if (expander is not null)
        {
            expander.IsExpanded = expanded;
        }

        _ = Dispatcher.InvokeAsync(UpdateStickyRegion, DispatcherPriority.Loaded);
    }

    private void UpdateStickyRegion()
    {
        AlignStickyRegionHeader();
        var headers = VisualDescendants<GroupItem>(ItemsList)
            .Where(item => item.IsVisible && item.DataContext is CollectionViewGroup)
            .Select(item => (
                Group: (CollectionViewGroup)item.DataContext,
                Top: item.TransformToAncestor(ItemsList).Transform(new Point()).Y))
            .OrderBy(item => item.Top)
            .ToArray();
        if (headers.Length == 0)
        {
            StickyRegionHeader.Visibility = Visibility.Collapsed;
            stickyRegion = null;
            return;
        }

        var passedHeader = headers
            .Where(item => item.Top <= 1)
            .OrderByDescending(item => item.Top)
            .FirstOrDefault();
        if (passedHeader.Group is null)
        {
            if (stickyRegion is not null)
            {
                return;
            }

            passedHeader = headers[0];
        }

        stickyRegion = passedHeader.Group.Name?.ToString();
        StickyRegionToggle.Content = passedHeader.Group;
        StickyRegionToggle.IsChecked = stickyRegion is not null
            && (!regionExpansion.TryGetValue(stickyRegion, out var expanded) || expanded);
        StickyRegionHeader.Visibility = Visibility.Visible;
    }

    private void AlignStickyRegionHeader()
    {
        var viewport = VisualDescendants<ScrollContentPresenter>(ItemsList).FirstOrDefault();
        if (viewport is null || viewport.ActualWidth <= 0)
        {
            return;
        }

        var position = viewport.TransformToAncestor(ItemsListHost).Transform(new Point());
        StickyRegionHeader.Margin = new Thickness(position.X, 0, 0, 0);
        StickyRegionHeader.Width = viewport.ActualWidth;
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in VisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void ShowEmptyState()
    {
        HideCandidateTrends();
        EditPricesButton.IsEnabled = false;
        SelectedNameText.Text = "等待记录";
        SelectedMetaText.Text = "打开物资详情页或地区调度页，然后开始识别";
        MainTrend.Values = null;
        PredictionMessageText.Text = "记录完整数据后显示预测结果";
        RequiredDaysBadge.Visibility = Visibility.Collapsed;
        ForecastChart.Values = null;
        ForecastChart.Visibility = Visibility.Collapsed;
        ForecastEmptyText.Visibility = Visibility.Visible;
        CandidateTrendsButton.Visibility = Visibility.Collapsed;
        CandidateTrendRows.ItemsSource = null;
    }

    private void EditPrices_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ItemRow row)
        {
            return;
        }

        try
        {
            var dialog = new PriceHistoryWindow(row.Name, store.GetDatedPrices(row.Name)) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Changes is null)
            {
                StatusText.Text = "已取消价格记录修改";
                return;
            }

            if (dialog.Changes.Count == 0)
            {
                StatusText.Text = "价格记录没有变化";
                return;
            }

            var count = store.ApplyPriceChanges(row.Name, dialog.Changes);
            RefreshItems(row.Name);
            StatusText.Text = $"已更新 {row.Name} 的 {count} 条价格记录";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "无法修改价格记录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DebugMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DebugMenuButton.ContextMenu is null)
        {
            return;
        }

        DebugMenuButton.ContextMenu.PlacementTarget = DebugMenuButton;
        DebugMenuButton.ContextMenu.IsOpen = true;
    }

    private void PredictionInfo_Click(object sender, RoutedEventArgs e) =>
        new PredictionInfoWindow { Owner = this }.ShowDialog();

    private void PurchasePlanner_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new PurchasePlannerWindow(store, predictionStatus) { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "无法打开购买建议", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private sealed record ItemRow(
        string Name,
        string IconPath,
        string Region,
        string Detail,
        int[] Prices,
        TrendDatum[] Trend,
        ItemSummary Summary);

    private sealed record CandidateTrendRow(
        string IndexText,
        int[] Prices,
        TrendDatum[] Trend);

}
