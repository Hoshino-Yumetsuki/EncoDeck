// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>Official product/driver model names. Recognition does not imply a vendor control protocol.</summary>
public static class UsbHeadsetProfiles
{
    // VH800PRO.Inf from Rapoo Headset Control 1.0.0.2. Never generalize this PID to other models.
    public static UsbHeadsetProfile? MatchHardware(IEnumerable<string> hardwareIds) => hardwareIds.Any(id =>
        Regex.IsMatch(id, @"^USB\\VID_24AE&PID_7205(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        ? Create().OfType<UsbHeadsetProfile>().Single(p => p.ModelId == "rapoo-usb-vh800-pro") : null;

    public static IReadOnlyList<IDeviceProfile> Create() =>
    [
        new UsbHeadsetProfile("edifier", "g2-wireless", "HECATE G2 无线版", ["HECATE G2", "G2无线7.1版"]),
        new UsbHeadsetProfile("edifier", "g3-wireless", "HECATE G3 无线版", ["HECATE G3"]),
        new UsbHeadsetProfile("edifier", "g3-max", "HECATE G3 Max", ["HECATE G3 Max 无线版"]),
        new UsbHeadsetProfile("edifier", "g4s", "HECATE G4S", []),
        new UsbHeadsetProfile("edifier", "g4-max", "HECATE G4 Max", []),
        new UsbHeadsetProfile("edifier", "g5-wireless", "HECATE G5 无线版", ["HECATE G5"]),
        new UsbHeadsetProfile("edifier", "g5-max", "HECATE G5 Max", []),
        new UsbHeadsetProfile("dareu", "a710", "DAREU A710", ["A710-WIRELESS"]),
        new UsbHeadsetProfile("dareu", "a710x", "DAREU A710X", ["A710X-WIRELESS"]),
        new UsbHeadsetProfile("dareu", "a3-pro", "DAREU A3 Pro", []),
        new UsbHeadsetProfile("dareu", "a3-max", "DAREU A3 Max", []),
        new UsbHeadsetProfile("dareu", "a5-pro", "DAREU A5 Pro", []),
        new UsbHeadsetProfile("dareu", "a5-max", "DAREU A5 Max", []),
        new UsbHeadsetProfile("dareu", "eh925-ultra", "DAREU EH925 ULTRA", []),
        new UsbHeadsetProfile("dareu", "eh722x-ultra", "DAREU EH722X ULTRA", []),
        new UsbHeadsetProfile("rapoo", "vh800", "RAPOO VH800", ["VH800"]),
        new UsbHeadsetProfile("rapoo", "vh800-pro", "RAPOO VH800Pro", ["VH800Pro", "VH800 Pro"]),
        new UsbHeadsetProfile("rapoo", "vh850", "RAPOO VH850", ["VH850"]),
    ];
}

public sealed class UsbHeadsetProfile(string brand, string model, string name, string[] aliases, bool batteryReady = false) : IDeviceProfile
{
    public string ModelId => $"{brand}-usb-{model}";
    public string DisplayName => name;
    public string ImageKey => ModelId;
    public DeviceForm Form => DeviceForm.Headphones;
    public UsbHeadsetProfile WithBattery(bool ready) => new(brand, model, name, aliases, ready);
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, batteryReady ? CapabilitySupport.Supported : CapabilitySupport.Unknown,
            "Read-only standard HID BatteryStrength/RelativeStateOfCharge, only when advertised as 0–100. Proprietary vendor reports are not inferred.").Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public IReadOnlyList<Guid> ControlServiceUuids => [];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Usb) return 0;
        return new[] { name }.Concat(aliases).Any(n => Regex.IsMatch(identity.Name,
            @"(?<![\p{L}\p{N}])" + Regex.Escape(n).Replace(@"\ ", @"[\s_-]*") + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ? 110 : 0;
    }
}
