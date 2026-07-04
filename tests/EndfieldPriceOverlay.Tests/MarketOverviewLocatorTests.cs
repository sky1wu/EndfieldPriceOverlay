using EndfieldPriceOverlay.Services;
using SkiaSharp;

namespace EndfieldPriceOverlay.Tests;

public sealed class MarketOverviewLocatorTests
{
    [Fact]
    public void LocatesCardRowsAtCurrentScrollPosition()
    {
        using var bitmap = new SKBitmap(2048, 1152);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(235, 235, 235));
        using var accent = new SKPaint { Color = new SKColor(165, 205, 12) };
        DrawCardBottoms(canvas, accent, y: 666, cards: 7);
        DrawCardBottoms(canvas, accent, y: 1014, cards: 5);

        var rows = MarketOverviewLocator.LocateRowBottoms(bitmap);

        Assert.Equal(666 / 1152d, rows[0], 3);
        Assert.Equal(1014 / 1152d, rows[1], 3);
    }

    [Fact]
    public void LocatesSecondRowWithOnlyTwoCards()
    {
        using var bitmap = new SKBitmap(2048, 1152);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(235, 235, 235));
        using var accent = new SKPaint { Color = new SKColor(165, 205, 12) };
        DrawCardBottoms(canvas, accent, y: 624, cards: 7);
        DrawCardBottoms(canvas, accent, y: 970, cards: 2);

        var rows = MarketOverviewLocator.LocateRowBottoms(bitmap);

        Assert.Equal(624 / 1152d, rows[0], 3);
        Assert.Equal(970 / 1152d, rows[1], 3);
    }

    private static void DrawCardBottoms(SKCanvas canvas, SKPaint paint, int y, int cards)
    {
        const float firstLeft = 0.055f * 2048;
        const float step = 0.119f * 2048;
        const float width = 0.111f * 2048;
        for (var index = 0; index < cards; index++)
        {
            canvas.DrawRect(firstLeft + index * step, y, width, 8, paint);
        }
    }
}
