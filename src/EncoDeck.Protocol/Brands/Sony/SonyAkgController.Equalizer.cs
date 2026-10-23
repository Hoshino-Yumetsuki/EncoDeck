// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sony;

internal sealed partial class SonyAkgController
{
    private byte? _eqType;
    private IReadOnlyList<EqualizerPreset> _equalizers = [];

    private async Task RefreshEqualizer(CancellationToken ct)
    {
        byte type = _eqType!.Value;
        if (_equalizers.Count == 0)
        {
            var capability = await Query(0x50, 0x51, type, ct, [type, 1]).ConfigureAwait(false);
            if (capability is null || !SonyEqualizer.TryPresets(capability, out _equalizers, !_v2)) return;
        }
        var value = await Query(0x56, 0x57, type, ct).ConfigureAwait(false);
        if (value is null || !SonyEqualizer.TryParameter(value, out _, !_v2)) return;
        Store.TryUpdate(DeviceId, s => s with { Profile = profile.WithNoiseSupport(_noiseReady).WithEqualizers(_equalizers) });
    }

    public override async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await _commands.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            if (!Sony || !_eqType.HasValue || !_equalizers.Any(p => p.Id == presetId)
                || !byte.TryParse(presetId.AsSpan(5), out byte id)) return NotSupported("EQ preset has not been reported by this device.");
            // The official app selects an existing preset with a zero-length band vector.
            if (!await Send(0x58, [_eqType.Value, id, 0], cancellationToken).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            var value = await Query(0x56, 0x57, _eqType.Value, cancellationToken).ConfigureAwait(false);
            if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            var result = value is null ? new CommandResult(CommandStatus.Timeout)
                : SonyEqualizer.TryParameter(value, out var actual, !_v2) && actual == id ? CommandResult.Ok
                : new CommandResult(CommandStatus.Rejected, "The device kept a different EQ preset.");
            Store.TryUpdate(DeviceId, s => s with
            {
                Equalizer = result.IsSuccess ? Observed.From(new EqualizerState(presetId, id), Now, StateSource.CommandConfirmation) : s.Equalizer,
                LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now),
            });
            return result;
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally { if (entered) _commands.Release(); }
    }
}
