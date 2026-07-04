using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using EndfieldPriceOverlay.Domain;
using RapidOcrNet;
using SkiaSharp;

namespace EndfieldPriceOverlay.Services;

public sealed partial class OcrService : IDisposable
{
    private readonly RapidOcr engine = new();
    private readonly LayoutConfigService layoutConfig;
    private bool initialized;

    public OcrService(LayoutConfigService layoutConfig)
    {
        this.layoutConfig = layoutConfig;
    }

    public OcrReading Recognize(BitmapSource source, ScreenLayout configuredLayout)
    {
        EnsureInitialized();
        using var bitmap = ToSkBitmap(source);
        var layout = layoutConfig.Effective(configuredLayout, bitmap.Width);
        using var nameCrop = Crop(bitmap, layout.Name);
        using var chartCrop = Crop(bitmap, layout.Chart);

        var nameBlocks = Detect(nameCrop);
        var nameBlock = nameBlocks
            .Where(block => HasChinese().IsMatch(block.Text))
            .Where(block => !IgnoredName().IsMatch(block.Text))
            .OrderBy(block => block.CenterY)
            .ThenByDescending(block => block.Score)
            .FirstOrDefault();

        var prices = new int?[7];
        var scores = new double?[7];
        for (var index = 0; index < 7; index++)
        {
            var left = (int)Math.Round(chartCrop.Width * index / 7d);
            var right = (int)Math.Round(chartCrop.Width * (index + 1) / 7d);
            using var column = new SKBitmap(right - left, chartCrop.Height);
            if (!chartCrop.ExtractSubset(column, new SKRectI(left, 0, right, chartCrop.Height)))
            {
                continue;
            }

            var candidate = Detect(column)
                .Select(block => (Block: block, Price: ParsePrice(block.Text)))
                .Where(item => item.Price is not null)
                .OrderByDescending(item => item.Block.Score)
                .FirstOrDefault();
            if (candidate.Price is not null)
            {
                prices[index] = candidate.Price;
                scores[index] = candidate.Block.Score;
            }
        }

        return new OcrReading(
            nameBlock?.Text ?? string.Empty,
            prices,
            DateTime.Now,
            nameBlock?.Score ?? 0,
            scores);
    }

    public void Dispose()
    {
        if (!initialized)
        {
            return;
        }

        engine.Dispose();
        initialized = false;
    }

    private void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        var baseDirectory = AppContext.BaseDirectory;
        engine.InitModels(
            detPath: Path.Combine(baseDirectory, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx"),
            clsPath: Path.Combine(baseDirectory, "models", "v5", "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
            recPath: Path.Combine(baseDirectory, "Models", "Ocr", "ch_PP-OCRv5_rec_mobile.onnx"),
            keysPath: Path.Combine(baseDirectory, "Models", "Ocr", "ppocrv5_dict.txt"));
        initialized = true;
    }

    private IReadOnlyList<RecognizedBlock> Detect(SKBitmap bitmap)
    {
        var options = RapidOcrOptions.Default with
        {
            DoAngle = false,
            TextScore = 0.35f,
            ImgResize = bitmap.Width >= 1000 ? 1600 : 1024,
        };
        var result = engine.Detect(bitmap, options);
        return result.TextBlocks.Select(block =>
        {
            var points = block.BoxPoints;
            var centerY = points.Average(point => point.Y);
            var score = block.CharScores is { Length: > 0 }
                ? block.CharScores.Average(value => (double)value)
                : 0;
            return new RecognizedBlock(block.Text.Trim(), score, centerY);
        }).ToArray();
    }

    private static int? ParsePrice(string text)
    {
        var normalized = text
            .Replace('O', '0')
            .Replace('o', '0')
            .Replace('I', '1')
            .Replace('l', '1')
            .Replace(",", string.Empty, StringComparison.Ordinal);
        return PriceNumber().Matches(normalized)
            .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))
            .LastOrDefault(value => value is >= 300 and <= 5500) is { } value and > 0
                ? value
                : null;
    }

    private static SKBitmap Crop(SKBitmap source, NormalizedRect area)
    {
        var rectangle = new SKRectI(
            area.PixelLeft(source.Width),
            area.PixelTop(source.Height),
            area.PixelLeft(source.Width) + area.PixelWidth(source.Width),
            area.PixelTop(source.Height) + area.PixelHeight(source.Height));
        var result = new SKBitmap(rectangle.Width, rectangle.Height);
        if (!source.ExtractSubset(result, rectangle))
        {
            result.Dispose();
            throw new InvalidOperationException("OCR 裁剪区域超出游戏窗口。");
        }

        return result;
    }

    private static SKBitmap ToSkBitmap(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return SKBitmap.Decode(stream) ?? throw new InvalidOperationException("无法转换游戏截图。");
    }

    private sealed record RecognizedBlock(string Text, double Score, double CenterY);

    [GeneratedRegex(@"[\u4e00-\u9fff]")]
    private static partial Regex HasChinese();

    [GeneratedRegex("需求物资|库存|拥有|购买|出售|今日售价|建议价格")]
    private static partial Regex IgnoredName();

    [GeneratedRegex(@"\d{3,4}")]
    private static partial Regex PriceNumber();
}
