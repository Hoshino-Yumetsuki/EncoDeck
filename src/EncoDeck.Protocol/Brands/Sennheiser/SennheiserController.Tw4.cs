// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sennheiser;

internal sealed partial class SennheiserController
{
    private bool? _ancEnabled, _transparencyEnabled;
    private byte? _wind;
    private bool _windReady, _smartPauseReady, _lowLatencyReady, _gesturesReady;

    private static bool Boolean(byte[] p) => p is [0 or 1];
    private static bool AncModes(byte[] p)
    {
        if (p.Length != 6 || !p.Where((_, i) => i % 2 == 0).Order().SequenceEqual(new byte[] { 1, 2, 3 })) return false;
        for (int i = 0; i < p.Length; i += 2) if (p[i + 1] > (p[i] == 1 ? 2 : 1)) return false;
        return true;
    }

    private async Task<bool> ReadTw4NoiseAsync(CancellationToken ct)
    {
        // 0x1803 reports the saved transparency *level*, even while transparency is off.
        // Only these independent status queries can establish the three-state selector.
        var anc = await Query(0x1A05, [], Boolean, ct).ConfigureAwait(false);
        var transparency = await Query(0x1805, [], Boolean, ct).ConfigureAwait(false);
        return anc is not null && transparency is not null;
    }

    private async Task RefreshTw4Async(CancellationToken ct)
    {
        _noiseReady = await ReadTw4NoiseAsync(ct).ConfigureAwait(false);
        _windReady = await Query(0x1A01, [], AncModes, ct).ConfigureAwait(false) is not null;
        if (!_windReady) { _wind = null; PublishTw4Noise(StateSource.QueryResponse); }
        _smartPauseReady = await Query(0x080D, [], Boolean, ct).ConfigureAwait(false) is not null;
        _lowLatencyReady = await Query(0x0818, [], Boolean, ct).ConfigureAwait(false) is not null;
        bool gestures = false;
        foreach (var side in new[] { EarSide.Left, EarSide.Right })
        foreach (var action in SennheiserGestures.Actions)
            gestures |= await ReadGestureAsync(side, action, ct).ConfigureAwait(false) is not null;
        _gesturesReady = gestures;
        Store.TryUpdate(DeviceId, s => s with { Profile = profile.WithControls(_noiseReady, _windReady, _smartPauseReady, _lowLatencyReady, _gesturesReady) });
    }

    private void PublishTw4Noise(StateSource source)
    {
        if (_ancEnabled is not bool anc || _transparencyEnabled is not bool transparency) return;
        var state = new NoiseControlState(transparency ? NoiseControlMode.Transparency : anc ? NoiseControlMode.NoiseCancellation : NoiseControlMode.Off,
            !transparency && anc && _wind is not null ? _wind > 0 ? "anti-wind" : "normal" : null);
        Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(state, Now, source) });
    }

    private bool HandleTw4Packet(BrandPacket packet, ushort command)
    {
        byte[] p = packet.Payload;
        var source = packet.Type == 1 ? StateSource.DeviceNotification : StateSource.QueryResponse;
        if (command == 0x1A05 && Boolean(p)) _ancEnabled = p[0] == 1;
        else if (command == 0x1805 && Boolean(p)) _transparencyEnabled = p[0] == 1;
        // Observed TW4 transparency notifications use the ANC feature's level notification.
        // Never apply this rule to a GET level or to the headband slider protocol.
        else if (packet.Type == 1 && command == 0x1A03 && p is [<= 100]) _transparencyEnabled = p[0] > 0;
        else if (command == 0x1A01 && AncModes(p))
        {
            for (int i = 0; i < p.Length; i += 2) if (p[i] == 1) _wind = p[i + 1];
        }
        else if (command is 0x080D or 0x0818 && Boolean(p))
        {
            var toggle = command == 0x080D ? DeviceToggle.InEarDetection : DeviceToggle.GamingMode;
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(p[0] == 1, Now, source)) });
            return true;
        }
        else if (packet.Type == 2 && command == 0x1601 && SennheiserGestures.ValidSlot(p))
        {
            var key = new KeyFunction(p[0] == 1 ? EarSide.Left : EarSide.Right, 0, (GestureAction)p[1], p[2]);
            Store.TryUpdate(DeviceId, s => s with
            {
                KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(
                    [.. (s.KeyFunctions?.Value ?? []).Where(k => k.Side != key.Side || k.Action != key.Action), key], Now, source),
            });
            return true;
        }
        else return false;
        PublishTw4Noise(source);
        return true;
    }

    private async Task<CommandResult> SetValueAsync(ushort command, byte[] value, CancellationToken ct)
    {
        var reply = await Exchange(command, value, p => p.Length == 0, ct).ConfigureAwait(false);
        return reply is null ? TimedOut : reply.Type == 3
            ? new(CommandStatus.Rejected, $"The device rejected command {command:X4}.") : CommandResult.Ok;
    }

    private async Task<CommandResult> SetTw4NoiseAsync(NoiseControlState target, CancellationToken ct)
    {
        if (!_noiseReady || target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency)
            || (target.LevelId is not null && (target.Mode != NoiseControlMode.NoiseCancellation || !_windReady || target.LevelId is not ("normal" or "anti-wind"))))
            return NotSupported("TW4 noise mode");
        await Operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            CommandResult result;
            if (target.Mode == NoiseControlMode.Transparency)
                result = await SetValueAsync(0x1804, [1], ct).ConfigureAwait(false);
            else
            {
                result = await SetValueAsync(0x1804, [0], ct).ConfigureAwait(false);
                if (result.IsSuccess && target.Mode == NoiseControlMode.NoiseCancellation && _windReady)
                    result = await SetValueAsync(0x1A00, [1, target.LevelId == "anti-wind" ? (byte)1 : (byte)0], ct).ConfigureAwait(false);
                if (result.IsSuccess)
                    result = await SetValueAsync(0x1A04, [target.Mode == NoiseControlMode.Off ? (byte)0 : (byte)1], ct).ConfigureAwait(false);
            }
            // Even a partial failure can change the device. Render its actual state, never the request/ACK.
            bool read = await ReadTw4NoiseAsync(ct).ConfigureAwait(false);
            if (_windReady) read &= await Query(0x1A01, [], AncModes, ct).ConfigureAwait(false) is not null;
            if (!result.IsSuccess) return result;
            if (!read) return TimedOut;
            bool matches = Store.Current.NoiseControl?.Value.Mode == target.Mode
                && (target.Mode != NoiseControlMode.NoiseCancellation || !_windReady || _wind == (target.LevelId == "anti-wind" ? 1 : 0));
            return matches ? CommandResult.Ok : new(CommandStatus.Rejected, "TW4 noise-control readback did not match.");
        }
        finally { Operations.Release(); }
    }

    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (!profile.IsTw4 || !(toggle == DeviceToggle.InEarDetection && _smartPauseReady || toggle == DeviceToggle.GamingMode && _lowLatencyReady))
            return NotSupported(toggle.ToString());
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            ushort set = toggle == DeviceToggle.InEarDetection ? (ushort)0x080C : (ushort)0x0817;
            var result = await SetValueAsync(set, [enabled ? (byte)1 : (byte)0], cancellationToken).ConfigureAwait(false);
            var read = await Query((ushort)(set + 1), [], Boolean, cancellationToken).ConfigureAwait(false);
            return !result.IsSuccess ? result : read is null ? TimedOut : (read.Payload[0] == 1) == enabled
                ? CommandResult.Ok : new(CommandStatus.Rejected, "TW4 setting readback did not match.");
        }
        finally { Operations.Release(); }
    }

    private Task<BrandPacket?> ReadGestureAsync(EarSide side, GestureAction action, CancellationToken ct) =>
        Query(0x1601, [SennheiserGestures.WireSide(side), (byte)action],
            p => SennheiserGestures.ValidSlot(p) && p[0] == SennheiserGestures.WireSide(side) && p[1] == (byte)action, ct);

    public override async Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default)
    {
        if (!profile.IsTw4 || !_gesturesReady || assignment.Side is not (EarSide.Left or EarSide.Right) || assignment.Button != 0
            || !SennheiserGestures.Functions(assignment.Action).Contains(assignment.Function)) return NotSupported("TW4 gesture");
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            // Re-read this exact slot before allowing a write. Unsupported/unknown slots stay untouched.
            if (await ReadGestureAsync(assignment.Side, assignment.Action, cancellationToken).ConfigureAwait(false) is null) return TimedOut;
            var result = await SetValueAsync(0x1600, [SennheiserGestures.WireSide(assignment.Side), (byte)assignment.Action, (byte)assignment.Function], cancellationToken).ConfigureAwait(false);
            var read = await ReadGestureAsync(assignment.Side, assignment.Action, cancellationToken).ConfigureAwait(false);
            return !result.IsSuccess ? result : read is null ? TimedOut : read.Payload[2] == assignment.Function
                ? CommandResult.Ok : new(CommandStatus.Rejected, "TW4 gesture readback did not match.");
        }
        finally { Operations.Release(); }
    }
}
