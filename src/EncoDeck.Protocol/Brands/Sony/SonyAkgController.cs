// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Sony;

internal sealed partial class SonyAkgController(IDuplexByteChannel channel, ControllerContext context, SonyAkgProfile profile)
    : BrandControllerBase(channel, context)
{
    private readonly SonyFrameCodec _sony = new();
    private readonly AkgFrameCodec _akg = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<byte[]>? _pending;
    private byte _expected, _subtype, _sequence;
    private TaskCompletionSource? _ack;
    private byte _ackSequence;
    private byte? _receivedSequence;
    private bool? _anc, _ambient;
    private bool _v2;
    private byte? _noiseType;
    private byte _batteryType;
    private byte? _caseType;
    private bool _noiseReady;
    private bool _wearReady;
    private bool Sony => profile.Brand == "sony";

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        if (Sony)
        {
            Logger.LogInformation("Starting Sony MDR protocol handshake");
            var protocol = await Query(0, 1, 0, cancellationToken).ConfigureAwait(false);
            if (protocol is null) return false;
            _v2 = protocol.Length == 8;
            Logger.LogInformation("Sony MDR protocol {Protocol} confirmed", _v2 ? "v2" : "v1");
            if (!_v2) _eqType = 1; // MDR v1 PRESET_EQ; v2 assigns the same feature type 0.
            if (_v2)
            {
                var functions = await Query(6, 7, 0, cancellationToken).ConfigureAwait(false);
                if (functions is null) return false;
                for (int i = 3; i + 1 < functions.Length; i += 2)
                {
                    _noiseType ??= SonyV2Noise.InquiredType(functions[i]);
                    _eqType ??= functions[i] switch { 80 => (byte)0, 82 => (byte)2, 87 => (byte)4, _ => null };
                    if (functions[i] == 40) _batteryType = 8;
                    if (functions[i] == 33) _batteryType = 1;
                    if (functions[i] == 41) _batteryType = 9;
                    if (functions[i] == 34) _caseType = 2;
                    if (functions[i] == 42) _caseType = 10;
                }
            }
        }
        return (await RefreshAsync(cancellationToken).ConfigureAwait(false)).Status == CommandStatus.Success;
    }

    private async Task<byte[]?> Query(byte command, byte expected, byte subtype, CancellationToken ct, byte[]? payload = null)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _pending = completion; _expected = expected; _subtype = subtype; }
        try
        {
            if (!await Send(command, payload ?? (command == 0x11 && !Sony ? [] : [subtype]), ct).ConfigureAwait(false)) return null;
            try
            {
                await Task.WhenAny(completion.Task, Closed).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                return completion.Task.IsCompletedSuccessfully ? await completion.Task.ConfigureAwait(false) : null;
            }
            catch (TimeoutException)
            {
                Logger.LogInformation("Control response timed out: command {Command:X2}, subtype {Subtype:X2}", command, subtype);
                return null;
            }
        }
        finally { lock (_gate) { if (_pending == completion) _pending = null; } }
    }

    private async Task<bool> Send(byte command, byte[] payload, CancellationToken ct)
    {
        if (!Sony) return await WriteAsync(AkgFrameCodec.Encode(command, payload), ct).ConfigureAwait(false);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _ack = ack; _ackSequence = (byte)(_sequence ^ 1); }
        try
        {
            // Sound Connect Bl.e / yl.c: 750 ms ACK timeout, at most ten retransmissions.
            // Retransmit the same frame and sequence; changing the bit could apply a SET twice.
            byte[] frame = SonyFrameCodec.Encode(0x0c, _sequence, [command, .. payload]);
            for (int attempt = 0; attempt <= 10; attempt++)
            {
                if (Closed.IsCompleted || !await WriteAsync(frame, ct).ConfigureAwait(false)) return false;
                try
                {
                    await Task.WhenAny(ack.Task, Closed).WaitAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
                    if (!ack.Task.IsCompletedSuccessfully || Closed.IsCompleted) return false;
                    _sequence ^= 1;
                    return true;
                }
                catch (TimeoutException)
                {
                    Logger.LogInformation("Sony ACK timeout: command {Command:X2}, sequence {Sequence}, attempt {Attempt}/11", command, _sequence, attempt + 1);
                }
            }
            // Like the official client, reconnect when ACK retries are exhausted.
            // The peer may have processed the request, so do not reuse its sequence bit.
            EndSession();
            return false;
        }
        catch (OperationCanceledException)
        {
            EndSession();
            throw;
        }
        finally { lock (_gate) { if (_ack == ack) _ack = null; } }
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            byte[]? battery = Sony
                ? await Query(_v2 ? (byte)0x22 : (byte)0x10, _v2 ? (byte)0x23 : (byte)0x11,
                    _v2 ? _batteryType : profile.Form == Core.Profiles.DeviceForm.Earbuds ? (byte)1 : (byte)0, cancellationToken).ConfigureAwait(false)
                : await Query(0x11, 0x12, 0, cancellationToken).ConfigureAwait(false);
            if (Sony && (!_v2 || _noiseType.HasValue))
                _noiseReady = await Query(0x66, 0x67, _v2 ? _noiseType!.Value : (byte)2, cancellationToken).ConfigureAwait(false) is not null;
            else if (!Sony)
            {
                var anc = await Query(0x21, 0x22, 0x31, cancellationToken).ConfigureAwait(false);
                var ambient = await Query(0x21, 0x22, 0x32, cancellationToken).ConfigureAwait(false);
                _noiseReady = anc is not null && ambient is not null;
                _wearReady = await Query(0x21, 0x22, 0x38, cancellationToken).ConfigureAwait(false) is not null;
            }
            Store.TryUpdate(DeviceId, s => s with { Profile = profile.WithNoiseSupport(_noiseReady).WithWearDetection(_wearReady) });
            if (Sony && _eqType.HasValue) await RefreshEqualizer(cancellationToken).ConfigureAwait(false);
            if (Sony && _v2 && _caseType.HasValue)
                await Query(0x22, 0x23, _caseType.Value, cancellationToken).ConfigureAwait(false);
            return battery is null ? new(CommandStatus.Timeout, "No battery response on the official control service.") : CommandResult.Ok;
        }
        finally { _commands.Release(); }
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (target.LevelId is not null || target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            return CommandResult.Unsupported("This protocol exposes off, noise cancelling and ambient sound only.");
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            if (!_noiseReady) return CommandResult.Unsupported("A compatible noise-control layout has not been confirmed on this device.");
            byte[]? response;
            if (Sony)
            {
                if (_v2 && !_noiseType.HasValue) return CommandResult.Unsupported("Device did not advertise an implemented noise-control layout.");
                byte type = _v2 ? _noiseType!.Value : (byte)2;
                var current = await Query(0x66, 0x67, type, cancellationToken).ConfigureAwait(false);
                if (current is null) return new(CommandStatus.Timeout, "Noise parameters unavailable.");
                byte[]? payload;
                if (_v2) payload = SonyV2Noise.SetPayload(current, target.Mode);
                else
                {
                    if (current.Length != 8) return CommandResult.Unsupported("Unknown noise parameter layout.");
                    payload = current[1..];
                    payload[1] = target.Mode == NoiseControlMode.Off ? (byte)0 : (byte)1;
                    payload[3] = target.Mode == NoiseControlMode.NoiseCancellation ? (payload[2] == 2 ? (byte)2 : (byte)1) : (byte)0;
                    payload[6] = target.Mode == NoiseControlMode.Transparency ? (byte)20 : (byte)0;
                }
                if (payload is null) return CommandResult.Unsupported("Unknown noise parameter layout.");
                if (!await Send(0x68, payload, cancellationToken).ConfigureAwait(false)) return new(CommandStatus.TransportError);
                response = await Query(0x66, 0x67, type, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                byte command = target.Mode == NoiseControlMode.Transparency ? (byte)0x32 : (byte)0x31;
                byte value = target.Mode == NoiseControlMode.Transparency ? (byte)2 : target.Mode == NoiseControlMode.NoiseCancellation ? (byte)1 : (byte)0;
                if (!await Send(command, [value], cancellationToken).ConfigureAwait(false))
                    return new(CommandStatus.TransportError);
                if (target.Mode == NoiseControlMode.Off && !await Send(0x32, [0], cancellationToken).ConfigureAwait(false)) return new(CommandStatus.TransportError);
                var anc = await Query(0x21, 0x22, 0x31, cancellationToken).ConfigureAwait(false);
                response = await Query(0x21, 0x22, 0x32, cancellationToken).ConfigureAwait(false);
                if (anc is null) response = null;
            }
            if (response is null) return new(CommandStatus.Timeout, "Write was not confirmed by a fresh device readback.");
            return Store.Current.NoiseControl?.Value.Mode == target.Mode ? CommandResult.Ok : new(CommandStatus.Rejected, "Device readback differs from requested mode.");
        }
        finally { _commands.Release(); }
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        if (Sony)
        {
            foreach (var frame in _sony.Feed(data))
            {
                if (frame.Type == 1 && frame.Payload.Length == 0)
                { lock (_gate) { if (frame.Sequence == _ackSequence) _ack?.TrySetResult(); } continue; }
                // Official DataType.ackRequired / Bl.f also ACK common, table-2 and
                // unimplemented DATA messages. Ignoring them stalls the peer's send queue.
                bool ackRequired = frame.Type is 0 or 2 or 7 or 9 or 10 or 12 or 13 or 14 or 39 or 45;
                bool shot = frame.Type is 16 or 18 or 25 or 26 or 28 or 29 or 30;
                if (!ackRequired && !shot) continue;
                if (ackRequired && !WriteAsync(SonyFrameCodec.Encode(1, (byte)(frame.Sequence ^ 1), []), CancellationToken.None).GetAwaiter().GetResult()) continue;
                if (_receivedSequence == frame.Sequence) continue;
                _receivedSequence = frame.Sequence;
                // Only table-1 payloads are parsed; other tables can reuse opcode numbers.
                if (frame.Type is 12 or 28 && frame.Payload.Length >= 2) Handle(frame.Payload);
            }
        }
        else foreach (var frame in _akg.Feed(data)) Handle([frame[1], .. frame.AsSpan(3)]);
    }

    private void Handle(byte[] p)
    {
        if (p.Length < 2) return;
        bool valid = false;
        if (Sony && p[0] == 0x51 && p[1] == _eqType && SonyEqualizer.TryPresets(p, out _, !_v2)) valid = true;
        else if (Sony && p[0] is 0x57 or 0x59 && p[1] == _eqType && SonyEqualizer.TryParameter(p, out var preset, !_v2))
        {
            Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState($"sony-{preset}", preset), Now, StateSource.QueryResponse) });
            valid = true;
        }
        else if (Sony && p[0] == 1 && p[1] == 0 && p.Length is 4 or 8) valid = true;
        else if (Sony && p[0] == 7 && p[1] == 0 && p.Length >= 3 && p.Length == 3 + p[2] * 2) valid = true;
        else if (Sony && ((!_v2 && p[0] is 0x11 or 0x13) || (_v2 && p[0] is 0x23 or 0x25)))
        {
            if (p[1] is 1 or 9 && p.Length >= 6) { Battery(BatteryComponent.Left, p[2], p[3]); Battery(BatteryComponent.Right, p[4], p[5]); valid = true; }
            else if (p[1] is 0 or 2 or 8 or 10 && p.Length >= 4) { Battery(p[1] is 2 or 10 ? BatteryComponent.Case : BatteryComponent.Left, p[2], p[3]); valid = true; }
        }
        else if (Sony && _v2 && p[0] is 0x67 or 0x69 && p[1] == _noiseType && SonyV2Noise.TryRead(p, out var mode))
        { Noise(mode); valid = true; }
        else if (Sony && !_v2 && p[0] is 0x67 or 0x69 && p.Length == 8 && p[1] == 2)
        {
            Noise(p[2] == 0 ? NoiseControlMode.Off : p[7] > 0 ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation); valid = true;
        }
        else if (!Sony && p[0] == 0x12 && p.Length >= 21)
        {
            Battery(BatteryComponent.Left, (byte)(p[20] & 0x7f), (byte)(p[20] >> 7)); valid = true;
        }
        else if (!Sony && p[0] == 0x22 && p.Length >= 3 && p[1] is 0x31 or 0x32)
        {
            if (p[1] == 0x31) _anc = p[2] == 1; else _ambient = p[2] is 1 or 2;
            if (_anc.HasValue && _ambient.HasValue) Noise(_ambient.Value ? NoiseControlMode.Transparency : _anc.Value ? NoiseControlMode.NoiseCancellation : NoiseControlMode.Off);
            valid = true;
        }
        else if (!Sony && p is [0x22, 0x38, 0 or 1])
        {
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(p[2] == 1, Now, StateSource.QueryResponse)) });
            valid = true;
        }
        if (!valid) return;
        Store.TryUpdate(DeviceId, s => s with { LastResponseAt = Now, LastUpdateSource = StateSource.QueryResponse });
        lock (_gate)
            if (p[0] == _expected && (p[1] == _subtype || (!Sony && p[0] == 0x12))) _pending?.TrySetResult(p);
    }
    private void Noise(NoiseControlMode mode) => Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(mode, null), Now, StateSource.QueryResponse) });
    private void Battery(BatteryComponent component, byte level, byte charging)
    {
        if (level > 100) return;
        Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With(component, Observed.From(new BatteryReading(level, charging == 1), Now, StateSource.QueryResponse)) });
    }
}
