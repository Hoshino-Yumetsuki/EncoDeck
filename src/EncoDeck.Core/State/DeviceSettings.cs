// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Boolean device-side settings. Each maps to a capability in the device profile.</summary>
public enum DeviceToggle
{
    /// <summary>Game mode master switch.</summary>
    GamingMode,
    InEarDetection,
    SpatialAudio,
    HeadTracking,
    DualDeviceConnection,
    /// <summary>Low game latency (sub-option of game mode).</summary>
    LowLatency,
    /// <summary>Pause playback when the wearer falls asleep.</summary>
    SleepDetection,
    /// <summary>Windows Swift Pair advertising.</summary>
    SwiftPair,
    /// <summary>On-earbud voice commands.</summary>
    VoiceCommands,
    /// <summary>Answer/decline calls by nodding or shaking the head.</summary>
    HeadGestures,
    /// <summary>BassWave bass engine.</summary>
    BassEngine,
    /// <summary>Hi-Res mode (read only on Windows; mutually exclusive with spatial audio).</summary>
    HiRes,
}

/// <summary>Firmware / model information read from the device.</summary>
public sealed record FirmwareInfo(string? Version, string? ModelCode, IReadOnlyDictionary<string, string> Extra)
{
    public static FirmwareInfo Empty { get; } = new(null, null, new Dictionary<string, string>());
}

/// <summary>Device-side equalizer state. This is the EQ inside the earbuds, never a Windows audio effect.</summary>
public sealed record EqualizerState(string? ActivePresetId, int? RawPresetValue);
