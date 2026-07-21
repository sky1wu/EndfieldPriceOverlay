using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EndfieldPriceOverlay.Domain;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay;

public partial class PurchasePlannerWindow : Window
{
    private readonly PurchaseSettingsService settingsService = new();
    private readonly PurchaseRecommendationService recommendation;
    private readonly PurchaseRegionField[] fields;
    private readonly string? focusedRegion;

    public PurchasePlannerWindow(CaptureStore store, PredictionStatusService predictionStatus, string? focusedRegion = null)
    {
        InitializeComponent();
        this.focusedRegion = focusedRegion;
        recommendation = new PurchaseRecommendationService(store, predictionStatus);
        var savedSettings = settingsService.Load().ToDictionary(setting => setting.Region, StringComparer.Ordinal);
        fields = ItemRegionCatalog.KnownRegions
            .Select(region => new PurchaseRegionField(savedSettings[region]))
            .ToArray();
        RegionFields.ItemsSource = fields;
        AdviceGroups.ItemsSource = Array.Empty<RegionAdviceView>();
        SummaryText.Text = "填写各地区数量后生成建议";
        Loaded += (_, _) =>
        {
            AnimateSection(SettingsSection, 0);
            AnimateSection(ResultsSection, 70);
            if (this.focusedRegion is not null)
            {
                GenerateAdvice(showError: true);
            }
        };
    }

    private void Generate_Click(object sender, RoutedEventArgs e) => GenerateAdvice(showError: true);

    private void GenerateAdvice(bool showError)
    {
        if (!TryBuildSettings(showError, out var settings))
        {
            return;
        }

        try
        {
            settingsService.Save(settings);
            var result = recommendation.Build(settings)
                .Where(item => focusedRegion is null || item.Region == focusedRegion)
                .Select(item => new RegionAdviceView(item))
                .ToArray();
            AdviceGroups.ItemsSource = result;
            AdviceEmptyState.Visibility = result.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            AdviceResultsScroller.Visibility = result.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (result.Length > 0)
            {
                AnimateResults();
            }

            var total = result.Sum(item => item.TotalQuantity);
            SummaryText.Text = $"已生成 {result.Count(item => item.IsReady)} 个地区建议 · 合计 {total} 件";
        }
        catch (Exception exception)
        {
            if (showError)
            {
                MessageBox.Show(this, exception.Message, "无法生成购买建议", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (TryBuildSettings(showError: false, out var settings))
        {
            try
            {
                settingsService.Save(settings);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        base.OnClosing(e);
    }

    private bool TryBuildSettings(bool showError, out IReadOnlyList<RegionPurchaseSettings> settings)
    {
        var parsed = new List<RegionPurchaseSettings>(fields.Length);
        foreach (var field in fields)
        {
            if (!TryParse(field, showError, out var setting))
            {
                settings = [];
                return false;
            }

            parsed.Add(setting);
        }

        settings = parsed;
        return true;
    }

    private bool TryParse(PurchaseRegionField field, bool showError, out RegionPurchaseSettings setting)
    {
        setting = default!;
        if (!TryParseNumber(field.Current, out var current))
        {
            ShowInputError(showError, $"{field.Region} 的当前可购买数量必须是非负整数。");
            return false;
        }

        if (!TryParseNumber(field.Limit, out var limit))
        {
            ShowInputError(showError, $"{field.Region} 的上限必须是非负整数。");
            return false;
        }

        if (!TryParseNumber(field.DailyRecovery, out var recovery))
        {
            ShowInputError(showError, $"{field.Region} 的每日恢复量必须是非负整数。");
            return false;
        }

        if (current > limit)
        {
            ShowInputError(showError, $"{field.Region} 的当前可购买数量不能超过上限。");
            return false;
        }

        setting = new RegionPurchaseSettings(field.Region, current, limit, recovery);
        return true;
    }

    private void ShowInputError(bool showError, string message)
    {
        if (showError)
        {
            MessageBox.Show(this, message, "无法生成购买建议");
        }
    }

    private static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number)
        && number >= 0;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void AnimateSection(FrameworkElement element, int delayMilliseconds)
    {
        var transform = new TranslateTransform(0, 8);
        element.RenderTransform = transform;
        element.Opacity = 0;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = delay,
            EasingFunction = easing,
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = delay,
            EasingFunction = easing,
        });
    }

    private void AnimateResults()
    {
        var transform = AdviceResultsScroller.RenderTransform as TranslateTransform ?? new TranslateTransform();
        AdviceResultsScroller.RenderTransform = transform;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        AdviceResultsScroller.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = easing,
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = easing,
        });
    }

    private sealed class PurchaseRegionField(RegionPurchaseSettings setting) : INotifyPropertyChanged
    {
        private string current = setting.Current.ToString(CultureInfo.InvariantCulture);
        private string limit = setting.Limit.ToString(CultureInfo.InvariantCulture);
        private string dailyRecovery = setting.DailyRecovery.ToString(CultureInfo.InvariantCulture);

        public string Region { get; } = setting.Region;

        public string Current
        {
            get => current;
            set
            {
                current = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
            }
        }

        public string Limit
        {
            get => limit;
            set
            {
                limit = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Limit)));
            }
        }

        public string DailyRecovery
        {
            get => dailyRecovery;
            set
            {
                dailyRecovery = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DailyRecovery)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class RegionAdviceView
    {
        public RegionAdviceView(RegionPurchaseRecommendation source)
        {
            Region = source.Region;
            TotalQuantity = source.TotalQuantity;
            IsReady = source.IsReady;
            TotalText = source.IsReady ? $"{source.TotalQuantity} 件" : "未生成";
            Message = source.Message;
            Lines = source.Lines.Select(line => new AdviceLineView(line)).ToArray();
            EmptyText = Lines.Count == 0 ? "暂无购买动作" : string.Empty;
        }

        public string Region { get; }

        public int TotalQuantity { get; }

        public bool IsReady { get; }

        public string TotalText { get; }

        public string Message { get; }

        public string EmptyText { get; }

        public IReadOnlyList<AdviceLineView> Lines { get; }
    }

    private sealed class AdviceLineView
    {
        public AdviceLineView(PurchaseRecommendationLine source)
        {
            DateText = $"{source.Date:MM/dd} {WeekdayName(source.Date)}";
            QuantityText = $"买入 {source.Quantity}";
            ItemName = source.ItemName;
            PriceText = source.IsRange
                ? $"{source.Minimum}～{source.Maximum}"
                : source.Price.ToString(CultureInfo.InvariantCulture);
            AvailableText = $"当日可买 {source.AvailableBeforePurchase}";
        }

        public string DateText { get; }

        public string QuantityText { get; }

        public string ItemName { get; }

        public string PriceText { get; }

        public string AvailableText { get; }
    }

    private static string WeekdayName(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
