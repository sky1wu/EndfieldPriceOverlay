using SkiaSharp;

namespace EndfieldPriceOverlay.Services;

public static class MarketOverviewLocator
{
    private static readonly NormalizedRect[] RowSearchBands =
    [
        new(0.045, 0.500, 0.890, 0.660),
        new(0.045, 0.800, 0.890, 0.960),
    ];

    public static IReadOnlyList<double> LocateRowBottoms(SKBitmap bitmap) =>
        RowSearchBands.Select(band => LocateAccentLine(bitmap, band)).ToArray();

    private static double LocateAccentLine(SKBitmap bitmap, NormalizedRect band)
    {
        var left = band.PixelLeft(bitmap.Width);
        var right = left + band.PixelWidth(bitmap.Width);
        var top = band.PixelTop(bitmap.Height);
        var bottom = top + band.PixelHeight(bitmap.Height);
        var bestY = -1;
        var bestScore = 0;
        for (var y = top; y < bottom; y++)
        {
            var score = 0;
            for (var x = left; x < right; x += 2)
            {
                if (IsCardAccent(bitmap.GetPixel(x, y)))
                {
                    score++;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestY = y;
            }
        }

        var sampledWidth = Math.Max(1, (right - left) / 2);
        if (bestY < 0 || bestScore < sampledWidth * 0.08)
        {
            throw new InvalidOperationException("无法定位物资卡片。请确保两排卡片的绿色底边完整可见。");
        }

        return bestY / (double)bitmap.Height;
    }

    private static bool IsCardAccent(SKColor color) =>
        color.Green >= 130
        && color.Green >= color.Red + 18
        && color.Red >= 65
        && color.Blue <= 110;
}
