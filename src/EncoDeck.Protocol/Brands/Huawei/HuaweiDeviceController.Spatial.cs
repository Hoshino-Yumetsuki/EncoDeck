// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Huawei;

internal sealed partial class HuaweiDeviceController
{
    // FeatureMbbState.INDEPENDENT_SPATIAL_AUDIO.getSwitchMbb() in the official audio plugin.
    private static readonly byte[] SpatialQuery = [1, 1, 24, 2, 0, 3, 0];
    private readonly SemaphoreSlim _operations = new(1, 1);

    internal static SpatialAudioMode? ParseSpatial(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<byte, byte[]>();
        while (!payload.IsEmpty)
        {
            if (payload.Length < 2 || payload.Length < 2 + payload[1]) return null;
            int length = payload[1];
            if (!fields.TryAdd(payload[0], payload.Slice(2, length).ToArray())) return null;
            payload = payload[(length + 2)..];
        }
        if (fields.ContainsKey(HuaweiCommands.ErrorTlv)
            || !fields.TryGetValue(1, out var feature) || feature is not [24]
            || !fields.TryGetValue(2, out var state) || state.Length != 1) return null;
        return state[0] switch { 0 => SpatialAudioMode.Off, 1 => SpatialAudioMode.HeadTracking, 2 => SpatialAudioMode.Fixed, _ => null };
    }

    private void ApplySpatial(byte[] payload, StateSource source)
    {
        if (ParseSpatial(payload) is not { } mode) return;
        Profile = Profile.WithSpatial();
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = Profile, Spatial = Observed.From(mode, Now, source), LastUpdateSource = source,
        });
    }

    private async Task<CommandResult> SerializedAsync(Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct)
    {
        bool entered = false;
        try
        {
            await _operations.WaitAsync(ct).ConfigureAwait(false); entered = true;
            return Closed.IsCompleted ? CommandResult.NotConnected : await action(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally { if (entered) _operations.Release(); }
    }

    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => SerializedAsync(async ct =>
    {
        if (!Profile.SpatialConfirmed) return NotSupported("Independent spatial audio has not been reported by this device.");
        byte value;
        switch (mode)
        {
            case SpatialAudioMode.Off: value = 0; break;
            case SpatialAudioMode.HeadTracking: value = 1; break;
            case SpatialAudioMode.Fixed: value = 2; break;
            default: return NotSupported("Spatial mode");
        }
        if (!await WriteAsync(_codec.Encode(HuaweiCommands.FeatureState, [1, 1, 24, 2, 1, value]), ct).ConfigureAwait(false))
            return new CommandResult(CommandStatus.TransportError);
        await Task.Delay(ReadBackDelay, Context.Time, ct).ConfigureAwait(false);
        var (frame, status) = await QueryAsync(HuaweiCommands.FeatureState, SpatialQuery, ct).ConfigureAwait(false);
        if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
        var actual = frame is { } response ? ParseSpatial(response.Payload) : null;
        var result = status != CommandStatus.Success ? new CommandResult(status)
            : actual == mode ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device did not confirm the requested spatial mode.");
        if (result.IsSuccess) ApplySpatial(frame!.Value.Payload, StateSource.CommandConfirmation);
        Store.TryUpdate(DeviceId, s => s with { LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) });
        return result;
    }, cancellationToken);
}
