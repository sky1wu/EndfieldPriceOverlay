using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace EndfieldPriceOverlay;

public sealed class TrendControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable),
        typeof(TrendControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double StrokeThickness { get; set; } = 1.5;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = Values?.Cast<object>()
            .Select(value => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray() ?? [];
        if (values.Length == 0 || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(35, 48, 41)), 1);
        drawingContext.DrawLine(gridPen, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));

        var minimum = values.Min();
        var span = Math.Max(1, values.Max() - minimum);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < values.Length; index++)
            {
                var x = values.Length == 1 ? ActualWidth / 2 : index * ActualWidth / (values.Length - 1);
                var y = ActualHeight - 3 - ((values[index] - minimum) / span * (ActualHeight - 6));
                if (index == 0)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    context.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geometry.Freeze();
        var stroke = new SolidColorBrush(Color.FromRgb(169, 229, 46));
        stroke.Freeze();
        drawingContext.DrawGeometry(null, new Pen(stroke, StrokeThickness), geometry);
    }
}
