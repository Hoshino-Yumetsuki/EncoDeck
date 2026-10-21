// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Resources;

/// <summary>Visual resources a device can have. Each is optional; the UI falls back gracefully.</summary>
public enum DeviceResourceRole
{
    /// <summary>Earbuds with case (main product image).</summary>
    Device,
    /// <summary>Charging case only.</summary>
    Case,
    LeftBud,
    RightBud,
    /// <summary>Connection pop-up animation (video, animated image or image).</summary>
    PopupAnimation,
    /// <summary>Night (dark theme) variant of the pop-up animation; falls back to <see cref="PopupAnimation"/>.</summary>
    PopupAnimationDark,
    /// <summary>
    /// 360° turntable of the device: an animated WebP whose frames are evenly spaced viewing
    /// angles, or a HeyMelody 3D scene (<c>.vfxms</c>) rendered into one. Shown on the home page, dragged to rotate.
    /// </summary>
    DeviceModel,
}

public enum DeviceResourceKind
{
    Image,
    AnimatedImage,
    Video,
}

/// <summary>Where a resolved resource came from (shown to the user in Settings).</summary>
public enum DeviceResourceOrigin
{
    Custom,
    /// <summary>Downloaded from EncoAPI and cached on this PC.</summary>
    Downloaded,
    /// <summary>Existing local resource cache from an earlier version.</summary>
    Official,
    ImportedPack,
    BundledPack,
}

public sealed record DeviceResourceFile(string Path, DeviceResourceKind Kind, DeviceResourceOrigin Origin, string? PackName);

/// <summary>Identifies the device a resource is for. Colour variants fall back to the default colour.</summary>
public sealed record DeviceResourceKey(string ModelId, uint? ProductId, int? ColorId)
{
    public string ProductIdText => ProductId is { } pid ? pid.ToString("X6") : string.Empty;
}

public interface IDeviceResourceProvider
{
    /// <summary>Raised when resources were added, removed or replaced.</summary>
    event EventHandler? Changed;

    DeviceResourceFile? Resolve(DeviceResourceKey key, DeviceResourceRole role);

    /// <summary>Copies <paramref name="sourceFile"/> as the user's custom resource for the model and role.</summary>
    Task SetCustomAsync(DeviceResourceKey key, DeviceResourceRole role, string sourceFile, CancellationToken cancellationToken = default);

    void ClearCustom(DeviceResourceKey key, DeviceResourceRole role);

    public static DeviceResourceKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".webm" or ".mov" or ".m4v" => DeviceResourceKind.Video,
        ".gif" or ".apng" => DeviceResourceKind.AnimatedImage,
        _ => DeviceResourceKind.Image,
    };

    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".mp4", ".webm", ".mov", ".m4v", ".vfxms" };
}
