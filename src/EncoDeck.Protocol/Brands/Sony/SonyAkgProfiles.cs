// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Sony;

public static class SonyAkgProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        .. SonyModelNames.All.Select(n => new SonyAkgProfile("sony", n,
            n.StartsWith("WF-", StringComparison.Ordinal) || n.StartsWith("LinkBuds", StringComparison.Ordinal))),
        .. new[] { "AKG N400NC", "AKG N700NCM2", "AKG N200NC", "AKG Y600" }
            .Select(n => new SonyAkgProfile("akg", n, n.Contains("N400", StringComparison.Ordinal))),
        new AkgArtworkProfile("akg-n700nc", "AKG N700NC", false),
        new Akg.AkgHybridProfile(true),
        new Akg.AkgHybridProfile(false),
    ];
}

/// <summary>Recognized official model with artwork; no speculative protocol writes.</summary>
internal sealed class AkgArtworkProfile(string id, string name, bool earbuds) : IDeviceProfile
{
    public string ModelId => id;
    public string DisplayName => name;
    public string ImageKey => id;
    public DeviceForm Form => earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public IDeviceCapabilities Capabilities => DeviceCapabilities.Empty;
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public IReadOnlyList<Guid> ControlServiceUuids => [];
    public int MatchScore(DeviceIdentity identity) => Normalize(identity.Name) == Normalize(name) ? 100 : 0;
    private static string Normalize(string? value) => new((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

public sealed class SonyAkgProfile : IControllableProfile
{
    private readonly CapabilitySupport _noiseSupport;
    private readonly IReadOnlyList<EqualizerPreset> _presets;
    private readonly bool _wearConfirmed;
    internal SonyAkgProfile(string brand, string name, bool earbuds, CapabilitySupport noiseSupport = CapabilitySupport.Experimental, IReadOnlyList<EqualizerPreset>? presets = null, bool wearConfirmed = false)
    { Brand = brand; DisplayName = name; Form = earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones; _noiseSupport = noiseSupport; _presets = presets ?? []; _wearConfirmed = wearConfirmed; }
    public SonyAkgProfile WithNoiseSupport(bool available) => new(Brand, DisplayName, Form == DeviceForm.Earbuds,
        available ? CapabilitySupport.Supported : CapabilitySupport.Unsupported, _presets, _wearConfirmed);
    internal SonyAkgProfile WithEqualizers(IReadOnlyList<EqualizerPreset> presets) => new(Brand, DisplayName, Form == DeviceForm.Earbuds, _noiseSupport, presets, _wearConfirmed);
    internal SonyAkgProfile WithWearDetection(bool confirmed) => new(Brand, DisplayName, Form == DeviceForm.Earbuds, _noiseSupport, _presets, confirmed);
    public IReadOnlyCollection<DeviceToggle> Toggles => _wearConfirmed ? [DeviceToggle.InEarDetection] : [];
    public string Brand { get; }
    public bool IsGeneric => false;
    public string ModelId => Brand + "-" + Normalize(DisplayName).Replace(Brand, "", StringComparison.Ordinal);
    public string DisplayName { get; }
    public string ImageKey => ModelId;
    public DeviceForm Form { get; }
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Official APK battery query; hardware validation pending.")
        .Set(DeviceCapability.NoiseCancellation, _noiseSupport, "Official APK noise layout; enabled only after a successful live parameter query.")
        .Set(DeviceCapability.Equalizer, _presets.Count > 0 ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Preset IDs and names queried from the device using official MDR EQ capability layout.")
        .Set(DeviceCapability.InEarDetection, _wearConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official BES auto-play/pause status 0x38; enabled after live readback.")
        .Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions =>
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", _noiseSupport),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", _noiseSupport),
        new(NoiseControlMode.Transparency, null, "Ambient sound", "NoiseMode_Transparency", _noiseSupport),
    ];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => _presets;
    public IReadOnlyList<Guid> ControlServiceUuids => Brand == "sony"
        ? [new("956c7b26-d49a-4ba8-b03f-b17d393cb6e2"), new("96cc203e-5068-46ad-b32d-e316f5e069ba")]
        : [new("00001101-0000-1000-8000-00805f9b34fb")];
    public int MatchScore(DeviceIdentity identity)
    {
        string name = Normalize(identity.Name);
        string expected = Normalize(DisplayName);
        // Exact official model names only; protocol generation is probed on connection.
        return name == expected || (Brand == "sony" && name == "sony" + expected) ? 100 : 0;
    }
    private static string Normalize(string? name) => new((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new SonyAkgController(channel, context, this);
}
