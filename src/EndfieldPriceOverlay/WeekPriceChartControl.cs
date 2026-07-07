using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EndfieldPriceOverlay;

public sealed record WeekPriceDatum(DateOnly Date, int? Price, int? Minimum = null, int? Maximum = null)
{
    public int? Low => Price ?? Minimum;

    public int? High => Price ?? Maximum;
}

public sealed class WeekPriceChartControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable),
        typeof(WeekPriceChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = ReadValues();
        if (values.Length == 0 || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var prices = values.SelectMany(value => new[] { value.Low, value.High }).OfType<int>().ToArray();
        if (prices.Length == 0)
        {
            return;
        }

        var plotLeft = 14d;
        var plotRight = Math.Max(plotLeft + 1, ActualWidth - 14);
        var plotTop = 24d;
        var plotBottom = Math.Max(plotTop + 1, ActualHeight - 28);
        var gridPen = new Pen(FrozenBrush(Color.FromRgb(35, 48, 41)), 1);
        for (var index = 0; index < 3; index++)
        {
            var y = plotTop + (plotBottom - plotTop) * index / 2;
            drawingContext.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
        }

        var minimum = prices.Min();
        var maximum = prices.Max();
        var padding = Math.Max(20, (maximum - minimum) * 0.12);
        var scaleMinimum = minimum - padding;
        var scaleMaximum = maximum + padding;
        var span = Math.Max(1, scaleMaximum - scaleMinimum);
        Point PointAt(int index, int price) => new(
            values.Length == 1
                ? (plotLeft + plotRight) / 2
                : plotLeft + index * (plotRight - plotLeft) / (values.Length - 1),
            plotBottom - (price - scaleMinimum) / span * (plotBottom - plotTop));

        var hasRange = values.Any(value => value.Low != value.High);
        if (hasRange)
        {
            DrawRange(drawingContext, values, PointAt);
        }

        var accent = FrozenBrush(Color.FromRgb(169, 229, 46));
        DrawSegments(drawingContext, values, value => value.High, PointAt, new Pen(accent, 2.2));
        if (hasRange)
        {
            DrawSegments(
                drawingContext,
                values,
                value => value.Low,
                PointAt,
                new Pen(FrozenBrush(Color.FromRgb(91, 168, 151)), 1.8));
        }

        DrawLabelsAndExtrema(drawingContext, values, PointAt, minimum, maximum);
    }

    private void DrawLabelsAndExtrema(
        DrawingContext drawingContext,
        WeekPriceDatum[] values,
        Func<int, int, Point> pointAt,
        int minimum,
        int maximum)
    {
        var secondary = FrozenBrush(Color.FromRgb(118, 138, 127));
        var primary = FrozenBrush(Color.FromRgb(234, 241, 236));
        var highest = FrozenBrush(Color.FromRgb(216, 169, 52));
        var lowest = FrozenBrush(Color.FromRgb(91, 168, 151));
        var background = FrozenBrush(Color.FromRgb(23, 32, 27));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var index = 0; index < values.Length; index++)
        {
            var dateText = CreateText(values[index].Date.ToString("MM/dd", CultureInfo.InvariantCulture), 10, secondary, pixelsPerDip, FontWeights.Normal);
            var x = values.Length == 1
                ? ActualWidth / 2
                : 14 + index * (ActualWidth - 28) / (values.Length - 1);
            drawingContext.DrawText(dateText, new Point(
                Math.Clamp(x - dateText.Width / 2, 0, Math.Max(0, ActualWidth - dateText.Width)),
                ActualHeight - dateText.Height - 2));

            var value = values[index];
            var valueText = value.Low is null || value.High is null
                ? "—"
                : value.Low == value.High
                    ? value.High.Value.ToString(CultureInfo.InvariantCulture)
                    : $"{value.Low.Value}～{value.High.Value}";
            if (value.High == maximum)
            {
                valueText += " 最高";
            }

            if (value.Low == minimum)
            {
                valueText += " 最低";
            }

            var valueBrush = value.High == maximum
                ? highest
                : value.Low == minimum
                    ? lowest
                    : primary;
            var priceText = CreateText(valueText, value.Low == value.High ? 10 : 9, valueBrush, pixelsPerDip, FontWeights.SemiBold);
            var priceX = Math.Clamp(x - priceText.Width / 2, 1, Math.Max(1, ActualWidth - priceText.Width - 1));
            var anchorY = value.High is null ? ActualHeight / 2 : pointAt(index, value.High.Value).Y;
            var priceY = anchorY - priceText.Height - 9;
            if (priceY < 1)
            {
                priceY = anchorY + 7;
            }

            drawingContext.DrawRoundedRectangle(
                background,
                null,
                new Rect(priceX - 3, priceY - 1, priceText.Width + 6, priceText.Height + 2),
                2,
                2);
            drawingContext.DrawText(priceText, new Point(priceX, priceY));
        }

        var highIndex = Array.FindIndex(values, value => value.High == maximum);
        var lowIndex = Array.FindIndex(values, value => value.Low == minimum);
        DrawExtremum(drawingContext, pointAt(highIndex, maximum), highest);
        if (minimum != maximum || lowIndex != highIndex)
        {
            DrawExtremum(drawingContext, pointAt(lowIndex, minimum), lowest);
        }
    }

    private static void DrawExtremum(
        DrawingContext drawingContext,
        Point point,
        Brush brush)
    {
        drawingContext.DrawEllipse(brush, new Pen(FrozenBrush(Color.FromRgb(11, 16, 14)), 2), point, 4.5, 4.5);
    }

    private static void DrawRange(
        DrawingContext drawingContext,
        WeekPriceDatum[] values,
        Func<int, int, Point> pointAt)
    {
        var indexes = Enumerable.Range(0, values.Length)
            .Where(index => values[index].Low is not null && values[index].High is not null)
            .ToArray();
        if (indexes.Length < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = indexes[0];
            context.BeginFigure(pointAt(first, values[first].High!.Value), true, true);
            foreach (var index in indexes.Skip(1))
            {
                context.LineTo(pointAt(index, values[index].High!.Value), true, false);
            }

            foreach (var index in indexes.Reverse())
            {
                context.LineTo(pointAt(index, values[index].Low!.Value), true, false);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(FrozenBrush(Color.FromArgb(32, 169, 229, 46)), null, geometry);
    }

    private static void DrawSegments(
        DrawingContext drawingContext,
        WeekPriceDatum[] values,
        Func<WeekPriceDatum, int?> selector,
        Func<int, int, Point> pointAt,
        Pen pen)
    {
        Point? previous = null;
        for (var index = 0; index < values.Length; index++)
        {
            var price = selector(values[index]);
            if (price is null)
            {
                previous = null;
                continue;
            }

            var current = pointAt(index, price.Value);
            if (previous is not null)
            {
                drawingContext.DrawLine(pen, previous.Value, current);
            }

            previous = current;
        }
    }

    private WeekPriceDatum[] ReadValues()
    {
        if (Values is null)
        {
            return [];
        }

        return Values.OfType<WeekPriceDatum>().OrderBy(value => value.Date).ToArray();
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
}
