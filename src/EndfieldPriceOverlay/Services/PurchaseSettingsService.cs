using System.IO;
using System.Text.Json;
using EndfieldPriceOverlay.Domain;

namespace EndfieldPriceOverlay.Services;

public sealed class PurchaseSettingsService
{
    private readonly string settingsPath;

    public PurchaseSettingsService(string? path = null)
    {
        settingsPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EndfieldPriceOverlay",
            "purchase-settings.json");
    }

    public IReadOnlyList<RegionPurchaseSettings> Load()
    {
        if (!File.Exists(settingsPath))
        {
            return DefaultSettings();
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<RegionPurchaseSettings>>(File.ReadAllText(settingsPath))
                ?? [];
            var byRegion = saved
                .Where(setting => ItemRegionCatalog.IsKnownRegion(setting.Region))
                .ToDictionary(setting => setting.Region, StringComparer.Ordinal);
            return DefaultSettings()
                .Select(defaultSetting => byRegion.TryGetValue(defaultSetting.Region, out var savedSetting)
                    && savedSetting.Current >= 0
                    && savedSetting.Limit >= 0
                    && savedSetting.DailyRecovery >= 0
                    && savedSetting.Current <= savedSetting.Limit
                        ? savedSetting
                        : defaultSetting)
                .ToArray();
        }
        catch (JsonException)
        {
            return DefaultSettings();
        }
        catch (IOException)
        {
            return DefaultSettings();
        }
    }

    public void Save(IReadOnlyList<RegionPurchaseSettings> settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SaveRegion(RegionPurchaseSettings setting)
    {
        if (!ItemRegionCatalog.IsKnownRegion(setting.Region)
            || setting.Current < 0
            || setting.Limit < 0
            || setting.DailyRecovery < 0
            || setting.Current > setting.Limit)
        {
            throw new ArgumentOutOfRangeException(nameof(setting), "购买额度设置无效。");
        }

        var settings = Load()
            .Select(saved => saved.Region == setting.Region ? setting : saved)
            .ToArray();
        Save(settings);
    }

    private static IReadOnlyList<RegionPurchaseSettings> DefaultSettings() =>
    [
        new(ItemRegionCatalog.ValleyIv, Current: 0, Limit: 960, DailyRecovery: 320),
        new(ItemRegionCatalog.Wuling, Current: 0, Limit: 340, DailyRecovery: 170),
    ];
}
