// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sony;

internal sealed partial class SonyMdrController
{
    private byte? _eqType;
    private IReadOnlyList<EqualizerPreset> _equalizers = [];

    private async Task RefreshEqualizer(CancellationToken ct)
    {
        byte type = _eqType!.Value;
        if (_equalizers.Count == 0)
        {
            var capability = await Query(0x50, 0x51, type, ct, [type, 1]).ConfigureAwait(false);
            if (capability is null || !SonyEqualizer.TryPresets(capability, out var presets, !_v2)) return;
            _equalizers = presets.Where(p => SonySettings.IsPresetAccepted(Model, byte.Parse(p.Id.AsSpan(5)))).ToArray();
            if (_equalizers.Count == 0) { _eqType = null; return; }
        }
        var value = await Query(0x56, 0x57, type, ct).ConfigureAwait(false);
        if (value is null || !SonyEqualizer.TryParameter(value, out _, !_v2)) return;
        _profile = _profile.WithEqualizers(_equalizers);
        PublishProfile();
    }

    public override async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        return await Exclusive(async ct =>
        {
            if (!_eqType.HasValue || !_equalizers.Any(p => p.Id == presetId)
                || !byte.TryParse(presetId.AsSpan(5), out byte id)) return NotSupported("EQ preset has not been reported by this device.");
            // A preset is selected with a zero-length band vector; band steps would define a custom EQ
            // instead (WF-LC900 2.0.3 then switches to CUSTOM, mos9527/SonyHeadphonesClient notes).
            if (!await Send(0x58, [_eqType.Value, id, 0], ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            var value = await Query(0x56, 0x57, _eqType.Value, ct).ConfigureAwait(false);
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
        }, cancellationToken).ConfigureAwait(false);
    }
}
