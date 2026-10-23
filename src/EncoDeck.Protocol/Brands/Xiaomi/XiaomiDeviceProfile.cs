// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Family-level profile for Xiaomi / Redmi earbuds. The reference has no per-model catalog, so the
/// earbuds are recognized by name only and features are declared once the earbuds answer for them.
/// </summary>
public sealed class XiaomiDeviceProfile : IControllableProfile
{
    private readonly string[] _nameMarkers;
    private readonly int _score;
    private readonly XiaomiModelFeatures? _model;
    private readonly IReadOnlySet<ushort> _confirmed;

    private XiaomiDeviceProfile(string modelId, string displayName, string[] nameMarkers, int score, bool gamingMode,
        XiaomiModelFeatures? model = null, IReadOnlySet<ushort>? confirmed = null)
    {
        _score = score;
        ModelId = modelId;
        DisplayName = displayName;
        _nameMarkers = nameMarkers;
        HasGamingMode = gamingMode;
        _model = model;
        _confirmed = confirmed ?? new HashSet<ushort>();
        var caps = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Battery query 0x02 answers 02 02 04 07 L R C (OppoPodsManager / MiBudsClient capture).")
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Unsupported, "The battery answer carries no charging flags.")
            .Set(DeviceCapability.NoiseCancellation, HasNoiseControl ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official Xiaomi APK config 11; available only after live readback.")
            .Set(DeviceCapability.NoiseCancellationLevel, HasNoiseControl && (_model?.NoiseLevels.Length ?? 0) > 0 ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Levels intersected across official regional catalogs.")
            .Set(DeviceCapability.Equalizer, HasEqualizer ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official model sound_effect list and live config 7.")
            .Set(DeviceCapability.DualDeviceConnection, HasConfig(4) ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official config 4, confirmed by live response.")
            .Set(DeviceCapability.SpatialAudio, HasConfig(29) ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official config 29 bit 0.")
            .Set(DeviceCapability.HeadTracking, HasConfig(29) ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official config 29 bit 3.")
            .Set(DeviceCapability.PromptVolume, HasConfig(161) ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Official config 161 with device-reported bounds.");
        caps = gamingMode
            ? caps.Set(DeviceCapability.GamingMode, CapabilitySupport.Supported, "The earbuds answered a read of low-latency config id 47.")
            : caps.Set(DeviceCapability.GamingMode, CapabilitySupport.Unknown, "Low-latency config id 47 has not been read from these earbuds yet.");
        Capabilities = caps.Build();
    }

    /// <summary>Generic Xiaomi earbuds (Xiaomi Buds, Mi True Wireless, FlipBuds, …).</summary>
    public static XiaomiDeviceProfile Xiaomi { get; } = new(
        "xiaomi-earbuds",
        "Xiaomi earbuds",
        ["Xiaomi", "Mi Buds", "Mi True", "Mi Air", "FlipBuds"],
        score: 1,
        gamingMode: false);

    /// <summary>Generic Redmi earbuds (Redmi Buds, Redmi AirDots, …). Wins over <see cref="Xiaomi"/> for names containing both.</summary>
    public static XiaomiDeviceProfile Redmi { get; } = new(
        "redmi-earbuds",
        "Redmi earbuds",
        ["Redmi"],
        score: 2,
        gamingMode: false);

    public string ModelId { get; }

    public string DisplayName { get; }

    public string Brand => "xiaomi";

    public bool IsGeneric => true;

    /// <summary>No product artwork; the app falls back to a generic illustration.</summary>
    public string ImageKey => ModelId;

    /// <summary>Whether the earbuds have answered for low-latency (game) mode.</summary>
    public bool HasGamingMode { get; }

    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. HasGamingMode ? new[] { DeviceToggle.GamingMode } : [],
        .. HasConfig(4) ? new[] { DeviceToggle.DualDeviceConnection } : [],
    ];
    public bool HasNoiseControl => HasConfig(11);
    public bool HasEqualizer => HasConfig(7) && (_model?.Equalizers.Length ?? 0) > 0;
    internal bool HasConfig(ushort id) => _confirmed.Contains(id);

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => !HasNoiseControl ? [] :
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
        .. (_model?.NoiseLevels ?? []).Select(n => new NoiseControlOption(NoiseControlMode.NoiseCancellation, $"nc-{n}", $"Level {n + 1}", $"Xiaomi_Level_{n}", CapabilitySupport.Supported)),
        .. (_model?.AmbientLevels ?? []).Select(n => new NoiseControlOption(NoiseControlMode.Transparency, $"ambient-{n}", $"Level {n + 1}", $"Xiaomi_Level_{n}", CapabilitySupport.Supported)),
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => !HasEqualizer ? [] :
        _model!.Equalizers.Where(n => n != 10).Select(n => new EqualizerPreset($"xiaomi-{n}", $"EQ {n}", $"Xiaomi_Eq_{n}", CapabilitySupport.Supported)).ToArray();

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [XiaomiProtocol.XiaoAiSppUuid];

    public int MatchScore(DeviceIdentity identity) =>
        Matches(identity.Name) ? _score : 0;

    /// <summary>True when <paramref name="name"/> contains one of this profile's name markers.</summary>
    public bool Matches(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _nameMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>This profile with low-latency mode declared (or not).</summary>
    public XiaomiDeviceProfile WithGamingMode(bool supported)
    {
        if (supported == HasGamingMode) return this;
        return new XiaomiDeviceProfile(ModelId, DisplayName, _nameMarkers, _score, supported, _model, _confirmed);
    }

    internal XiaomiDeviceProfile ForDevice(string name) => new(ModelId, DisplayName, _nameMarkers, _score, HasGamingMode, XiaomiModelFeatures.Find(name), _confirmed);
    internal XiaomiDeviceProfile WithConfig(ushort id) => new(ModelId, DisplayName, _nameMarkers, _score,
        HasGamingMode || id == 47, _model, new HashSet<ushort>(_confirmed) { id });

    public IControllableProfile Remember(DeviceState known) =>
        WithGamingMode(HasGamingMode || known.Toggles.ContainsKey(DeviceToggle.GamingMode));

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new XiaomiDeviceController(channel, context, this);
}

/// <summary>Built-in Xiaomi-family profiles.</summary>
public static class XiaomiProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() => [XiaomiDeviceProfile.Xiaomi, XiaomiDeviceProfile.Redmi];
}
