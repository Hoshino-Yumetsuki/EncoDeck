// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary><see cref="IControllableProfile"/> for one vivo / iQOO model (or the generic fallback).</summary>
public sealed class VivoDeviceProfile : IControllableProfile
{
    private const string CatalogEvidence = "Declared by the official vivo model configuration.";
    private const string ProtocolEvidence = "Implemented for every vivo GAIA device.";

    private readonly string _normalizedName;
    private readonly string? _normalizedProject;

    public VivoDeviceProfile(VivoModel model, bool isGeneric = false)
    {
        Model = model;
        IsGeneric = isGeneric;
        _normalizedName = VivoModel.Normalize(model.Name);
        _normalizedProject = model.ProjectName is { } p ? VivoModel.Normalize(p) : null;
        Capabilities = BuildCapabilities(model, isGeneric);
        NoiseControlOptions = model.Noise
            ?
            [
                new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
                new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
                new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
            ]
            : [];
        EqualizerPresets = model.AudioEffectIds
            .Select(id =>
            {
                var (english, key) = VivoCatalog.EffectName(id);
                return new EqualizerPreset(VivoCatalog.PresetId(id), english, key, CapabilitySupport.Supported);
            })
            .ToArray();
    }

    public VivoModel Model { get; }

    public string Brand => "vivo";

    public bool IsGeneric { get; }

    public string ModelId => Model.ModelId;

    public string DisplayName => Model.DisplayName;

    public string ImageKey => Model.ModelId;

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [VivoConstants.ServiceUuid];

    /// <summary>
    /// 100 for the exact official name, 80 when the name carries the model's unique project code
    /// (e.g. <c>DPD2321A LE</c>), 40–70 when it contains the official name (longer names win), and
    /// 5 for the generic profile on any <c>vivo TWS</c> / <c>iQOO TWS</c> / vivo-iQOO headphones name.
    /// </summary>
    public int MatchScore(DeviceIdentity identity)
    {
        string name = VivoModel.Normalize(identity.Name);
        if (name.Length == 0) return 0;
        if (IsGeneric) return IsFamilyName(name) ? 5 : 0;
        if (name == _normalizedName) return 100;
        if (_normalizedProject is not null && Tokens(identity.Name).Contains(_normalizedProject)) return 80;
        if (name.Contains(_normalizedName, StringComparison.Ordinal)) return 40 + Math.Min(_normalizedName.Length, 30);
        return 0;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new VivoDeviceController(channel, context, this);

    internal static bool IsFamilyName(string normalized) =>
        normalized.StartsWith("vivotws", StringComparison.Ordinal)
        || normalized.StartsWith("iqootws", StringComparison.Ordinal)
        || normalized.StartsWith("vivoheadphones", StringComparison.Ordinal)
        || normalized.StartsWith("iqooheadphones", StringComparison.Ordinal);

    private static HashSet<string> Tokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = VivoModel.Normalize(part);
            if (normalized.Length > 0) tokens.Add(normalized);
        }
        return tokens;
    }

    private static DeviceCapabilities BuildCapabilities(VivoModel model, bool isGeneric)
    {
        var b = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, ProtocolEvidence)
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, ProtocolEvidence)
            .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, ProtocolEvidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "No adaptive mode in the vivo protocol.")
            .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.SpatialAudio, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.HeadTracking, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.TouchCustomization, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.DualDeviceConnection, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.FitTest, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.PromptVolume, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.BassEngine, CapabilitySupport.Unsupported, "Not implemented for vivo.");

        // Live wear state is only meaningful on earbuds.
        b.Set(DeviceCapability.InEarDetection, model.Headphones || isGeneric ? CapabilitySupport.Unknown : CapabilitySupport.Supported, ProtocolEvidence);
        b.Set(DeviceCapability.NoiseCancellation, Declared(model.Noise), CatalogEvidence);
        b.Set(DeviceCapability.Equalizer, Declared(model.AudioEffect > 0), CatalogEvidence);
        b.Set(DeviceCapability.FindMyEarbuds, Declared(model.Find), CatalogEvidence);
        b.Set(DeviceCapability.GamingMode, Declared(model.LowLatency), CatalogEvidence);
        return b.Build();

        CapabilitySupport Declared(bool yes) => yes
            ? CapabilitySupport.Supported
            : isGeneric ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported;
    }
}

/// <summary>Built-in vivo / iQOO profiles.</summary>
public static class VivoProfiles
{
    /// <summary>One profile per catalog model plus the generic fallback.</summary>
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. VivoCatalog.All.Select(m => new VivoDeviceProfile(m)), new VivoDeviceProfile(VivoCatalog.Generic, isGeneric: true)];
}
