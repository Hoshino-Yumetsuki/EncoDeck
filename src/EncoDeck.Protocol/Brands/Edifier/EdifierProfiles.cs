// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>Built-in EDIFIER profiles: known SPP models plus a name-only family fallback.</summary>
public static class EdifierProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        new EdifierProfile("edifier-w820nb", "EDIFIER W820NB", "w820nb", isGeneric: false),
        new EdifierProfile("edifier-w200bt", "EDIFIER W200BT", "w200bt", isGeneric: false),
        new EdifierProfile("edifier", "EDIFIER headphones", "edifier", isGeneric: true),
    ];

    /// <summary>Lower-case letters and digits only, so "W820NB Plus" becomes "w820nbplus".</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>One EDIFIER model (or the generic family). Battery and noise control only.</summary>
public sealed class EdifierProfile : IControllableProfile
{
    private const string Evidence = "EDIFIER SPP protocol (mEDIFIER via OppoPodsManager).";
    private readonly string _nameKey;

    internal EdifierProfile(string modelId, string displayName, string nameKey, bool isGeneric)
    {
        ModelId = modelId;
        DisplayName = displayName;
        _nameKey = nameKey;
        IsGeneric = isGeneric;
    }

    public string Brand => "edifier";

    public bool IsGeneric { get; }

    public string ModelId { get; }

    public string DisplayName { get; }

    public string ImageKey => ModelId;

    public IDeviceCapabilities Capabilities { get; } = DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
        .Set(DeviceCapability.NoiseCancellation, CapabilitySupport.Supported, Evidence)
        .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Unsupported, "Not reported by the protocol.")
        .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Not in the protocol.")
        .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Unsupported, "Not in the protocol.")
        .Set(DeviceCapability.Equalizer, CapabilitySupport.Unsupported, "Not implemented.")
        .Build();

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; } =
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; } = [];

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [EdifierConstants.SppServiceId];

    /// <summary>Exact model name scores 100, a variant ("W820NB Plus") 90, the family fallback 10.</summary>
    public int MatchScore(DeviceIdentity identity)
    {
        string name = EdifierProfiles.Normalize(identity.Name);
        if (name.Length == 0) return 0;
        if (IsGeneric) return name.Contains(_nameKey, StringComparison.Ordinal) ? 10 : 0;
        string withoutBrand = name.StartsWith("edifier", StringComparison.Ordinal) ? name["edifier".Length..] : name;
        if (withoutBrand == _nameKey) return 100;
        return name.Contains(_nameKey, StringComparison.Ordinal) ? 90 : 0;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new EdifierDeviceController(channel, context);

    public override string ToString() => ModelId;
}
