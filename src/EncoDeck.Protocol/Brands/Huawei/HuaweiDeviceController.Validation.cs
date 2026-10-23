// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.Huawei;

internal sealed partial class HuaweiDeviceController
{
    private bool IsDeviceProbeResponse(ushort command, Dictionary<byte, byte[]> fields) => command switch
    {
        HuaweiCommands.Battery or HuaweiCommands.BatteryAlt =>
            fields.TryGetValue(2, out var levels) && levels.Length is >= 1 and <= 3 && levels.Any(v => v <= 100)
            || fields.TryGetValue(1, out var overall) && overall.Length == 1 && overall[0] <= 100,
        HuaweiCommands.QueryAnc => ParseNoise(fields) is not null,
        _ => false,
    };
}
