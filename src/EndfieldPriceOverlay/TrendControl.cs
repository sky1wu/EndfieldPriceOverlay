using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EndfieldPriceOverlay;

public sealed record TrendDatum(DateOnly Date, int Price);

public sealed class TrendControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable),
        typeof(TrendControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels),
        typeof(bool),
        typeof(TrendControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid),
        typeof(bool),
        typeof(TrendControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public double StrokeThickness { get; set; } = 1.5;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = ReadValues();
        if (values.Length == 0 || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var plotLeft = ShowLabels ? 12d : 0d;
        var plotRight = ShowLabels ? Math.Max(plotLeft + 1, ActualWidth - 12) : ActualWidth;
        var plotTop = ShowLabels ? 28d : 3d;
        var plotBottom = ShowLabels ? Math.Max(plotTop + 1, ActualHeight - 30) : ActualHeight - 3;
        if (ShowGrid)
        {
            var gridBrush = FrozenBrush(Color.FromRgb(35, 48, 41));
            var gridPen = new Pen(gridBrush, 1);
            for (var index = 0; index < 3; index++)
            {
                var y = plotTop + (plotBottom - plotTop) * index / 2;
                drawingContext.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
            }
        }

        var minimum = values.Min(value => value.Price);
        var span = Math.Max(1, values.Max(value => value.Price) - minimum);
        var points = values.Select((value, index) => new Point(
            values.Length == 1
                ? (plotLeft + plotRight) / 2
                : plotLeft + index * (plotRight - plotLeft) / (values.Length - 1),
            plotBottom - ((value.Price - minimum) / span * (plotBottom - plotTop)))).ToArray();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            foreach (var point in points.Skip(1))
            {
                context.LineTo(point, true, false);
            }
        }

        geometry.Freeze();
        var accent = FrozenBrush(Color.FromRgb(169, 229, 46));
        drawingContext.DrawGeometry(null, new Pen(accent, StrokeThickness), geometry);

        if (!ShowLabels)
        {
            return;
        }

        var primaryText = FrozenBrush(Color.FromRgb(234, 241, 236));
        var secondaryText = FrozenBrush(Color.FromRgb(118, 138, 127));
        var labelBackground = FrozenBrush(Color.FromRgb(23, 32, 27));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var index = 0; index < values.Length; index++)
        {
            var point = points[index];
            var priceText = CreateText(values[index].Price.ToString(CultureInfo.InvariantCulture), 11, primaryText, pixelsPerDip, FontWeights.SemiBold);
            var labelY = point.Y - 24;
            if (labelY < 1)
            {
                labelY = point.Y + 7;
            }

            var labelX = Math.Clamp(point.X - priceText.Width / 2, 1, Math.Max(1, ActualWidth - priceText.Width - 1));
            drawingContext.DrawRoundedRectangle(
                labelBackground,
                null,
                new Rect(labelX - 4, labelY - 2, priceText.Width + 8, priceText.Height + 4),
                2,
                2);
            drawingContext.DrawText(priceText, new Point(labelX, labelY));
            drawingContext.DrawEllipse(accent, new Pen(FrozenBrush(Color.FromRgb(11, 16, 14)), 2), point, index == values.Length - 1 ? 5 : 3.5, index == values.Length - 1 ? 5 : 3.5);

            if (values[index].Date is not { } date)
            {
                continue;
            }

            var dateText = CreateText(date.ToString("MM/dd", CultureInfo.InvariantCulture), 10, secondaryText, pixelsPerDip, FontWeights.Normal);
            var dateX = Math.Clamp(point.X - dateText.Width / 2, 0, Math.Max(0, ActualWidth - dateText.Width));
            drawingContext.DrawText(dateText, new Point(dateX, ActualHeight - dateText.Height - 2));
        }
    }

    private DataPoint[] ReadValues()
    {
        if (Values is null)
        {
            return [];
        }

        var result = new List<DataPoint>();
        foreach (var value in Values)
        {
            switch (value)
            {
                case TrendDatum trend:
                    result.Add(new DataPoint(trend.Date, trend.Price));
                    break;
                case KeyValuePair<DateOnly, int> pair:
                    result.Add(new DataPoint(pair.Key, pair.Value));
                    break;
                case not null:
                    result.Add(new DataPoint(null, Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return result.ToArray();
    }

    private static FormattedText CreateText(
        string text,
        double size,
        Brush brush,
        double pixelsPerDip,
        FontWeight weight) => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            pixelsPerDip);

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record DataPoint(DateOnly? Date, double Price);
}
