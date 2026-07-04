using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndfieldPriceOverlay.Services;

public sealed record NormalizedRect(double Left, double Top, double Right, double Bottom)
{
    public int PixelLeft(int width) => (int)Math.Round(Left * width);

    public int PixelTop(int height) => (int)Math.Round(Top * height);

    public int PixelWidth(int width) => (int)Math.Round((Right - Left) * width);

    public int PixelHeight(int height) => (int)Math.Round((Bottom - Top) * height);
}

public sealed record ScreenLayout(
    [property: JsonPropertyName("name_roi")] NormalizedRect Name,
    [property: JsonPropertyName("chart_roi")] NormalizedRect Chart)
{
    public static ScreenLayout Default1080 { get; } = new(
        new(0.385, 0.260, 0.625, 0.325),
        new(0.500, 0.445, 0.865, 0.650));

    public static ScreenLayout Default4K { get; } = new(
        new(0.370, 0.245, 0.625, 0.325),
        new(0.472, 0.445, 0.810, 0.650));
}

public sealed class LayoutConfigService
{
    private readonly string configPath;

    public LayoutConfigService(string? path = null)
    {
        configPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EndfieldPriceOverlay",
            "config.json");
    }

    public ScreenLayout Load()
    {
        if (!File.Exists(configPath))
        {
            return ScreenLayout.Default1080;
        }

        try
        {
            return JsonSerializer.Deserialize<ScreenLayout>(File.ReadAllText(configPath))
                ?? ScreenLayout.Default1080;
        }
        catch (JsonException)
        {
            return ScreenLayout.Default1080;
        }
    }

    public ScreenLayout Effective(ScreenLayout layout, int screenshotWidth) =>
        screenshotWidth >= 3000 && layout == ScreenLayout.Default1080
            ? ScreenLayout.Default4K
            : layout;

    public void Save(ScreenLayout layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
    }
}
