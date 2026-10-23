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
    public static UsbHeadsetProfile? MatchHardware(IEnumerable<string> hardwareIds)
    {
        foreach (string id in hardwareIds)
        {
            var match = Regex.Match(id, @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            ushort vendor = Convert.ToUInt16(match.Groups[1].Value, 16), product = Convert.ToUInt16(match.Groups[2].Value, 16);
            string? model = vendor == 0x24AE && product == 0x7205 ? "rapoo-usb-vh800-pro" : UsbVendorBatteryProtocol.Model(vendor, product);
            if (model is not null) return Create().OfType<UsbHeadsetProfile>().Single(p => p.ModelId == model);
        }
        return null;
    }

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
        new UsbHeadsetProfile("mchose", "x9", "MCHOSE X9", ["迈从 X9"]),
        new UsbHeadsetProfile("mchose", "v9", "MCHOSE V9", ["迈从 V9"]),
        new UsbHeadsetProfile("mchose", "v9-pro", "MCHOSE V9 Pro", ["迈从 V9 Pro"]),
        new UsbHeadsetProfile("mchose", "v9-turbo-plus", "MCHOSE V9 Turbo+", ["MCHOSE V9Turbo+", "迈从 V9 Turbo+"]),
        new UsbHeadsetProfile("mchose", "s9-pro", "MCHOSE S9 Pro", ["迈从 S9 Pro"]),
        new UsbHeadsetProfile("mchose", "g9-pro", "MCHOSE G9 Pro", ["迈从 G9 Pro"]),
        new UsbHeadsetProfile("mchose", "r9", "MCHOSE R9", ["迈从 R9"]),
        new UsbHeadsetProfile("razer", "blackshark-v2-pro-2020", "Razer BlackShark V2 Pro (2020)", []),
        new UsbHeadsetProfile("razer", "blackshark-v2-pro-2023", "Razer BlackShark V2 Pro (2023)", []),
        new UsbHeadsetProfile("razer", "blackshark-v2-hyperspeed", "Razer BlackShark V2 HyperSpeed", []),
        new UsbHeadsetProfile("razer", "blackshark-v3-pro", "Razer BlackShark V3 Pro", []),
        new UsbHeadsetProfile("razer", "barracuda-x", "Razer Barracuda X", []),
        new UsbHeadsetProfile("razer", "barracuda-pro", "Razer Barracuda Pro", []),
        new UsbHeadsetProfile("razer", "kraken-v3-pro", "Razer Kraken V3 Pro", []),
        new UsbHeadsetProfile("razer", "kraken-v4", "Razer Kraken V4", []),
        new UsbHeadsetProfile("razer", "kraken-v4-pro", "Razer Kraken V4 Pro", []),
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
            "Read-only standard HID percentage, or a documented query gated by exact receiver VID/PID and HID collection. No EQ, lighting or firmware writes.").Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public IReadOnlyList<Guid> ControlServiceUuids => [];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Usb) return 0;
        return new[] { name }.Concat(aliases).Where(n => Regex.IsMatch(identity.Name,
            @"(?<![\p{L}\p{N}])" + Regex.Escape(n).Replace(@"\ ", @"[\s_-]*") + @"(?![\p{L}\p{N}])(?![\s_-]+(?:Pro|Max|Plus|X|Chroma|Turbo|Lite|Ultra|HyperSpeed)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(n => 110 + n.Length).DefaultIfEmpty(0).Max();
    }
}
