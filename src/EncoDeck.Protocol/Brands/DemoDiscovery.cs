// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Brands.Xiaomi;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// Profiles as a successful live session would leave them, for Bluetooth-free UI demos. Several
/// brands only declare features after the earbuds answer; this applies the catalog's answers.
/// It is never hardware evidence and the Bluetooth runtime never calls it.
/// </summary>
public static class DemoDiscovery
{
    // Ids and names as a Sony preset capability answer reports them.
    private static readonly EqualizerPreset[] SonyPresets =
    [
        new("sony-0", "Off", "", CapabilitySupport.Supported),
        new("sony-16", "Bright", "", CapabilitySupport.Supported),
        new("sony-17", "Excited", "", CapabilitySupport.Supported),
        new("sony-18", "Mellow", "", CapabilitySupport.Supported),
    ];

    public static IDeviceProfile AfterSession(IDeviceProfile profile, string deviceName) => profile switch
    {
        SonyAkgProfile sony => sony.WithNoiseSupport(true).WithWearDetection(sony.Form == DeviceForm.Earbuds)
            .WithEqualizers(sony.Brand == "sony" ? SonyPresets : []),
        XiaomiDeviceProfile xiaomi => new ushort[] { 4, 7, 11, 29, 47, 161 }
            .Aggregate(xiaomi.ForDevice(deviceName), (p, id) => p.WithConfig(id)),
        HuaweiDeviceProfile huawei => huawei.WithSpatial(),
        MoondropProfile moondrop => moondrop.WithFeatures(moondrop.CatalogFeatures()),
        _ => profile,
    };
}
