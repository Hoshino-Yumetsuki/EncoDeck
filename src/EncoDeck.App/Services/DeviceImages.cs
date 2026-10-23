// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.App.Services;

/// <summary>
/// Resolves optional per-model images from <c>Assets\Devices\{imageKey}.png</c>.
/// Manufacturer artwork is downloaded through EncoAPI; missing images retain the generic illustration.
/// </summary>
internal static class DeviceImages
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string? imageKey)
    {
        if (string.IsNullOrWhiteSpace(imageKey)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(imageKey, out var cached)) return cached;
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Devices", imageKey + ".png");
            string? result = File.Exists(path) ? path : null;
            Cache[imageKey] = result;
            return result;
        }
    }
}
