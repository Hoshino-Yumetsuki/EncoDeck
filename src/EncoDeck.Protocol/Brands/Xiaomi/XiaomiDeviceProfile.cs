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

    private XiaomiDeviceProfile(string modelId, string displayName, string[] nameMarkers, int score, bool gamingMode)
    {
        _score = score;
        ModelId = modelId;
        DisplayName = displayName;
        _nameMarkers = nameMarkers;
        HasGamingMode = gamingMode;
        var caps = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Battery query 0x02 answers 02 02 04 07 L R C (OppoPodsManager / MiBudsClient capture).")
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Unsupported, "The battery answer carries no charging flags.")
            .Set(DeviceCapability.NoiseCancellation, CapabilitySupport.Unsupported, "Noise mode values for config id 10 are not confirmed.")
            .Set(DeviceCapability.Equalizer, CapabilitySupport.Unsupported, "EQ values for config id 7 are not confirmed.");
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

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];

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
        var plain = ModelId == Redmi.ModelId ? Redmi : Xiaomi;
        return supported ? new XiaomiDeviceProfile(plain.ModelId, plain.DisplayName, plain._nameMarkers, plain._score, true) : plain;
    }

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
