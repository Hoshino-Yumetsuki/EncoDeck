// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Core.State;

/// <summary>
/// Immutable snapshot of everything EncoDeck knows about the active device.
/// Every surface (main window, tray panel, connection popup) renders from this single source.
/// </summary>
public sealed record DeviceState
{
    public static DeviceState Empty { get; } = new();

    public DeviceIdentity? Device { get; init; }

    public IDeviceProfile? Profile { get; init; }

    public DeviceConnectionState Connection { get; init; } = DeviceConnectionState.NotDetected;

    public BatteryStatus Battery { get; init; } = BatteryStatus.Unknown;

    public Observed<WearStatus>? Wear { get; init; }

    public Observed<NoiseControlState>? NoiseControl { get; init; }

    public Observed<EqualizerState>? Equalizer { get; init; }

    public ImmutableDictionary<DeviceToggle, Observed<bool>> Toggles { get; init; } = ImmutableDictionary<DeviceToggle, Observed<bool>>.Empty;

    public Observed<FirmwareInfo>? Firmware { get; init; }

    /// <summary>24-bit product id reported by the device (e.g. 0x06EC10 for Enco X4).</summary>
    public uint? ProductId { get; init; }

    /// <summary>Colour variant reported by the device; selects colour-specific product images.</summary>
    public int? ColorId { get; init; }

    /// <summary>
    /// Raw control-capability bitmap the earbuds last reported. Remembered across sessions so the
    /// settings offered (which depend on it) are the same from the start of every connection.
    /// </summary>
    public byte[]? ControlCapabilities { get; init; }

    /// <summary>Lower of the two earbuds' running firmware versions, as last reported (remembered like <see cref="ControlCapabilities"/>).</summary>
    public int? BudFirmware { get; init; }

    public Observed<SpatialAudioMode>? Spatial { get; init; }

    public Observed<GameSoundState>? GameSound { get; init; }

    /// <summary>Prompt (alert sound) volume 1–10.</summary>
    public Observed<int>? PromptVolume { get; init; }

    public Observed<BassEngineState>? BassEngine { get; init; }

    public Observed<IReadOnlyList<KeyFunction>>? KeyFunctions { get; init; }

    public Observed<NoiseCycleState>? NoiseCycle { get; init; }

    /// <summary>Level chosen by smart (auto) noise cancelling right now, e.g. "deep".</summary>
    public string? SmartLevelId { get; init; }

    public Observed<IReadOnlyList<SourceDevice>>? SourceDevices { get; init; }

    public Observed<PreferredDeviceState>? PreferredDevice { get; init; }

    public Observed<IReadOnlyList<CustomEqualizer>>? CustomEqualizers { get; init; }

    /// <summary>Head gestures for calls: 0 = nod answers / shake declines, 1 = the reverse.</summary>
    public Observed<int>? HeadMotionType { get; init; }

    public FitTestResult? FitTest { get; init; }

    public bool IsFitTestRunning { get; init; }

    public bool IsFindingEarbuds { get; init; }

    public ImmutableList<PendingCommand> PendingCommands { get; init; } = [];

    /// <summary>When the device last sent us anything on the control channel.</summary>
    public DateTimeOffset? LastResponseAt { get; init; }

    public StateSource? LastUpdateSource { get; init; }

    public DeviceError? LastError { get; init; }

    /// <summary>Monotonic revision, incremented by the store on every change.</summary>
    public long Revision { get; init; }

    public bool IsPending(string descriptionPrefix) =>
        PendingCommands.Any(p => p.Description.StartsWith(descriptionPrefix, StringComparison.Ordinal));
}
