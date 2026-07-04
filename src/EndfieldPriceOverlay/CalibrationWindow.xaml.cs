using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EndfieldPriceOverlay.Services;

namespace EndfieldPriceOverlay;

public partial class CalibrationWindow : Window
{
    private readonly BitmapSource screenshot;
    private Point start;
    private bool dragging;
    private NormalizedRect? nameArea;

    public CalibrationWindow(BitmapSource screenshot)
    {
        InitializeComponent();
        this.screenshot = screenshot;
        ScreenshotImage.Source = screenshot;
    }

    public ScreenLayout? Layout { get; private set; }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        start = ClampToImage(e.GetPosition(SelectionCanvas));
        dragging = true;
        ActiveRectangle.Visibility = Visibility.Visible;
        SetRectangle(ActiveRectangle, start, start);
        SelectionCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragging)
        {
            SetRectangle(ActiveRectangle, start, ClampToImage(e.GetPosition(SelectionCanvas)));
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging)
        {
            return;
        }

        dragging = false;
        SelectionCanvas.ReleaseMouseCapture();
        var end = ClampToImage(e.GetPosition(SelectionCanvas));
        if (Math.Abs(end.X - start.X) < 20 || Math.Abs(end.Y - start.Y) < 12)
        {
            ActiveRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        var area = ToNormalized(start, end);
        if (nameArea is null)
        {
            nameArea = area;
            CopyRectangle(ActiveRectangle, SavedNameRectangle);
            SavedNameRectangle.Visibility = Visibility.Visible;
            ActiveRectangle.Visibility = Visibility.Collapsed;
            InstructionText.Text = "第 2/2 步：框选包含 7 个价格的完整走势图区域";
            return;
        }

        Layout = new ScreenLayout(nameArea, area);
        DialogResult = true;
    }

    private Rect DisplayedImageRect()
    {
        var width = SelectionCanvas.ActualWidth;
        var height = SelectionCanvas.ActualHeight;
        var scale = Math.Min(width / screenshot.PixelWidth, height / screenshot.PixelHeight);
        var imageWidth = screenshot.PixelWidth * scale;
        var imageHeight = screenshot.PixelHeight * scale;
        return new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
    }

    private Point ClampToImage(Point point)
    {
        var bounds = DisplayedImageRect();
        return new Point(
            Math.Clamp(point.X, bounds.Left, bounds.Right),
            Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
    }

    private NormalizedRect ToNormalized(Point first, Point second)
    {
        var bounds = DisplayedImageRect();
        var left = (Math.Min(first.X, second.X) - bounds.Left) / bounds.Width;
        var top = (Math.Min(first.Y, second.Y) - bounds.Top) / bounds.Height;
        var right = (Math.Max(first.X, second.X) - bounds.Left) / bounds.Width;
        var bottom = (Math.Max(first.Y, second.Y) - bounds.Top) / bounds.Height;
        return new NormalizedRect(left, top, right, bottom);
    }

    private static void SetRectangle(Rectangle rectangle, Point first, Point second)
    {
        Canvas.SetLeft(rectangle, Math.Min(first.X, second.X));
        Canvas.SetTop(rectangle, Math.Min(first.Y, second.Y));
        rectangle.Width = Math.Abs(second.X - first.X);
        rectangle.Height = Math.Abs(second.Y - first.Y);
    }

    private static void CopyRectangle(Rectangle source, Rectangle target)
    {
        Canvas.SetLeft(target, Canvas.GetLeft(source));
        Canvas.SetTop(target, Canvas.GetTop(source));
        target.Width = source.Width;
        target.Height = source.Height;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
