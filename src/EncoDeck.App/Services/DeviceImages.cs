// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.App.Services;

/// <summary>
/// Resolves per-model product images from <c>Assets\Devices\{imageKey}.png</c>. EncoDeck ships
/// no manufacturer artwork; when no image is present the UI draws its own generic illustration.
/// Contributors may only add images they have the right to distribute (see CONTRIBUTING.md).
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
