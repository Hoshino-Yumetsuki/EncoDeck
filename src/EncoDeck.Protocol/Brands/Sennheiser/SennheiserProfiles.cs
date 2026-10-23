// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Sennheiser;

public static class SennheiserProfiles
{
    public static IReadOnlyList<IDeviceProfile> Create() =>
    [new SennheiserProfile("m4", "MOMENTUM 4", false, true, ["MOMENTUM 4 Wireless"]),
     new SennheiserProfile("ac1", "ACCENTUM", false, true, ["ACCENTUM Wireless"]),
     new SennheiserProfile("acp1", "ACCENTUM Plus", false, true, ["ACCENTUM Plus Wireless"]),
     new SennheiserProfile("tw3", "MOMENTUM TW 3", true, false, ["MOMENTUM True Wireless 3"]),
     new SennheiserProfile("tw4", "MOMENTUM TW 4", true, false, ["MOMENTUM TW4", "MOMENTUM True Wireless 4"]),
     new SennheiserProfile("atw1", "ACCENTUM True Wireless", true, false, ["ACCENTUM TW"]),
     new SennheiserProfile("msport1", "MOMENTUM Sport", true, false, []),
     new SennheiserProfile("cx200", "CX True Wireless", true, false, []),
     new SennheiserProfile("cx200sports", "SPORT True Wireless", true, false, []),
     new SennheiserProfile("cx400ii", "CX Plus True Wireless", true, false, ["CX Plus SE True Wireless"]),
     new SennheiserProfile("cx500", "CX 500", true, false, [])];
}

public sealed class SennheiserProfile(string code, string name, bool earbuds, bool slider, string[] aliases, bool noiseReady = false) : IControllableProfile
{
    public static Guid GattServiceId { get; } = new("63331337-23c1-11e5-b696-feff819cdc9f");
    public string Brand => "sennheiser";
    public string ModelId => "sennheiser-" + code;
    public string DisplayName => name;
    public string ImageKey => ModelId;
    public DeviceForm Form => earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public bool IsGeneric => false;
    public SennheiserProfile WithNoise(bool ready) => new(code, name, earbuds, slider, aliases, ready);
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Smart Control 4.9.3 GAIA v3 model configuration; hardware validation pending.")
        .Set(DeviceCapability.NoiseCancellation, noiseReady ? CapabilitySupport.Supported : CapabilitySupport.Unknown,
            "Headband transparency endpoints only after valid live readback; no ANC-off command is inferred.").Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => noiseReady ?
    [new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
     new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported)] : [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public IReadOnlyList<Guid> ControlServiceUuids => [new("a2129ff3-081b-4c45-8afe-469d9c4842ec"), GattServiceId];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Bluetooth) return 0;
        var actual = Normalize(identity.Name);
        return new[] { name }.Concat(aliases).Any(n => actual == Normalize(n) || actual == Normalize("Sennheiser " + n)) ? 100 : 0;
    }
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new SennheiserController(channel, context, this, earbuds, slider);
}
