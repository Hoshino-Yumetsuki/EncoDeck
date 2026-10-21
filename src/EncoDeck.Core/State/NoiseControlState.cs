// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

public enum NoiseControlMode
{
    Off,
    NoiseCancellation,
    Transparency,
    /// <summary>Device-side adaptive / "smart" noise cancellation.</summary>
    Adaptive,
}

/// <summary>Noise control as reported by the device.</summary>
/// <param name="Mode">Top-level mode.</param>
/// <param name="LevelId">Profile-defined level id (e.g. "light"), or null when the mode has no levels or the level is unknown.</param>
public sealed record NoiseControlState(NoiseControlMode Mode, string? LevelId = null);
