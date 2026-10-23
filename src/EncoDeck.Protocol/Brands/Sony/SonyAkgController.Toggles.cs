// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sony;

internal sealed partial class SonyAkgController
{
    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await _commands.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            if (Sony || toggle != DeviceToggle.InEarDetection || !_wearReady) return NotSupported(toggle.ToString());
            if (!await Send(0x35, [enabled ? (byte)1 : (byte)0], cancellationToken).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            var response = await Query(0x21, 0x22, 0x38, cancellationToken).ConfigureAwait(false);
            if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            var result = response is null ? new CommandResult(CommandStatus.Timeout)
                : (response[2] == 1) == enabled ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device kept its previous auto-play/pause setting.");
            Store.TryUpdate(DeviceId, s => s with
            {
                Toggles = result.IsSuccess ? s.Toggles.SetItem(toggle, Observed.From(enabled, Now, StateSource.CommandConfirmation)) : s.Toggles,
                LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now),
            });
            return result;
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally { if (entered) _commands.Release(); }
    }
}
