// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Resources;

/// <summary>Do not replay old downloaded first-pair tutorials as reconnection animations.</summary>
public static class HuaweiArtworkPolicy
{
    public static DeviceResourceFile? ForRole(DeviceResourceFile? file, DeviceResourceRole role)
    {
        if (file is null || file.Origin is DeviceResourceOrigin.Custom or DeviceResourceOrigin.ImportedPack) return file;
        string name = System.IO.Path.GetFileName(file.Path);
        bool valid = role switch
        {
            DeviceResourceRole.PopupAnimation or DeviceResourceRole.PopupAnimationDark => name.Contains(".reconnect.", StringComparison.OrdinalIgnoreCase),
            DeviceResourceRole.DeviceAnimation or DeviceResourceRole.DeviceAnimationDark => name.EndsWith(".device-once.mp4", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
        return valid ? file : null;
    }
}
