// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary><see cref="IControllableProfile"/> for one HUAWEI model (or the generic HUAWEI fallback).</summary>
public sealed class HuaweiDeviceProfile : IControllableProfile
{
    private const string Evidence = "OppoPodsManager/OpenFreebuds per-model table; not verified on hardware by EncoDeck.";

    public HuaweiDeviceProfile(HuaweiModel model, bool spatialConfirmed = false)
    {
        Model = model;
        NoiseControlOptions = BuildNoiseOptions(model);
        EqualizerPresets = [.. model.EqPresets.Select(p => new EqualizerPreset(p.PresetId, p.DisplayName, p.ResourceKey, CapabilitySupport.Supported))];
        SpatialConfirmed = spatialConfirmed;
        Capabilities = BuildCapabilities(model, spatialConfirmed);
    }

    public HuaweiModel Model { get; }
    public bool SpatialConfirmed { get; }
    internal HuaweiDeviceProfile WithSpatial() => SpatialConfirmed ? this : new(Model, true);

    public string Brand => "huawei";

    public bool IsGeneric => Model.IsGeneric;

    public string ModelId => Model.ModelId;

    public string DisplayName => Model.DisplayName;

    public string ImageKey => Model.ModelId;

    public DeviceForm Form => Model.HasChargingCase ? DeviceForm.Earbuds : DeviceForm.Headphones;

    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. Model.WearDetection ? [DeviceToggle.InEarDetection] : Array.Empty<DeviceToggle>(),
        .. Model.LowLatency ? [DeviceToggle.GamingMode] : Array.Empty<DeviceToggle>(),
        .. Model.DualConnect ? [DeviceToggle.DualDeviceConnection] : Array.Empty<DeviceToggle>(),
    ];

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [HuaweiCommands.SppServiceUuid];

    public int MatchScore(DeviceIdentity identity)
    {
        if (Model.IsGeneric) return HuaweiModels.IsFamilyName(identity.Name) ? 1 : 0;
        var detected = HuaweiModels.Detect(identity.Name, out bool exact);
        if (!ReferenceEquals(detected, Model)) return 0;
        return exact ? 100 : 50;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new HuaweiDeviceController(channel, context, this, HuaweiWire.CreateCodec(Model.Protocol));

    private static NoiseControlOption[] BuildNoiseOptions(HuaweiModel model)
    {
        if (!model.NoiseControlUsable) return [];
        var options = new List<NoiseControlOption>
        {
            new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        };
        if (model.AncLevels.Count > 0)
        {
            options.AddRange(model.AncLevels.Select(l => new NoiseControlOption(NoiseControlMode.NoiseCancellation, l.LevelId, l.DisplayName, l.ResourceKey, CapabilitySupport.Supported)));
        }
        else
        {
            options.Add(new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported));
        }
        if (model.Transparency)
        {
            options.Add(new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported));
        }
        return [.. options];
    }

    private static DeviceCapabilities BuildCapabilities(HuaweiModel model, bool spatialConfirmed)
    {
        static CapabilitySupport S(bool on) => on ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;
        return DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, S(model.RfcommBattery), Evidence)
            .Set(DeviceCapability.ChargingStatus, S(model.RfcommBattery), Evidence)
            .Set(DeviceCapability.NoiseCancellation, S(model.NoiseControlUsable), Evidence)
            .Set(DeviceCapability.NoiseCancellationLevel, S(model.NoiseControlUsable && model.AncLevels.Count > 1), Evidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Dynamic ANC is exposed as a noise-cancelling level.")
            .Set(DeviceCapability.Equalizer, S(model.EqPresets.Count > 0), Evidence)
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "HUAWEI custom EQ is not ported.")
            .Set(DeviceCapability.InEarDetection, S(model.WearDetection), Evidence)
            .Set(DeviceCapability.GamingMode, S(model.LowLatency), Evidence)
            .Set(DeviceCapability.DualDeviceConnection, S(model.DualConnect), Evidence)
            .Set(DeviceCapability.SpatialAudio, S(spatialConfirmed), "Official audio plugin 16.0.9.330 MBB feature 24, enabled after live readback.")
            .Set(DeviceCapability.HeadTracking, S(spatialConfirmed), "Independent spatial audio mode 1, confirmed by device.")
            .Set(DeviceCapability.FindMyEarbuds, CapabilitySupport.Unsupported, "MBB find-device TLVs are unverified upstream.")
            .Build();
    }
}

/// <summary>Built-in HUAWEI profiles.</summary>
public static class HuaweiProfiles
{
    /// <summary>Every model in <see cref="HuaweiModels.All"/> plus the generic fallback.</summary>
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. HuaweiModels.All.Select(m => new HuaweiDeviceProfile(m)), new HuaweiDeviceProfile(HuaweiModels.Generic)];
}
