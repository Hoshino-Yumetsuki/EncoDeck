// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

public sealed class AkgHybridProfile(bool earbuds, bool noiseReady = false) : IControllableProfile
{
    public string Brand => "akg";
    public string ModelId => earbuds ? "akg-n5hybrid" : "akg-n9hybrid";
    public string DisplayName => earbuds ? "AKG N5 Hybrid" : "AKG N9 Hybrid";
    public string ImageKey => ModelId;
    public DeviceForm Form => earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public bool IsGeneric => false;
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Official Airoha RACE battery queries; Windows hardware validation pending.")
        .Set(DeviceCapability.NoiseCancellation, noiseReady ? CapabilitySupport.Supported : CapabilitySupport.Unknown,
            "Enabled only after a valid six- or twelve-byte ANC module readback.").Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => noiseReady ?
    [new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
     new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
     new(NoiseControlMode.Transparency, null, "Ambient sound", "NoiseMode_Transparency", CapabilitySupport.Supported)] : [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public IReadOnlyList<Guid> ControlServiceUuids =>
        [new("00000000-0000-0000-0099-aabbccddeeff"), new("00001101-0000-1000-8000-00805f9b34fb")];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Bluetooth) return 0;
        string actual = Normalize(identity.Name);
        string prefix = earbuds ? "akgn5" : "akgn9";
        return actual == prefix || actual == prefix + "hybrid" ? 110 : 0;
    }
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new AkgHybridController(channel, context, earbuds);
}
