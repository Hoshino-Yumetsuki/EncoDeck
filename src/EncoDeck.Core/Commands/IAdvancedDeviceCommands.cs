// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Core.Commands;

public enum MultiConnectOperation
{
    Connect = 1,
    Disconnect = 2,
    /// <summary>Remove (unpair) a device that is not connected.</summary>
    Remove = 3,
}

/// <summary>
/// Device-changing commands beyond the basics. Every implementation confirms with the earbuds and
/// re-reads the affected state, the same way the official client does.
/// </summary>
public interface IAdvancedDeviceCommands
{
    Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default);

    Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default);

    Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default);

    Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default);

    Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default);

    Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default);

    Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default);

    Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default);

    /// <summary>Plays the fit-test tone and waits for the result (stored in <see cref="DeviceState.FitTest"/>).</summary>
    Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default);

    /// <summary>Sets the preferred source device; null means automatic selection.</summary>
    Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default);

    Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default);

    Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default);

    Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default);
}
