// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

internal sealed class AkgHybridController(IDuplexByteChannel channel, ControllerContext context, bool earbuds)
    : RequestReplyController(channel, context)
{
    private readonly RaceFrameCodec _codec = new();
    private bool? _agentRight;
    private byte[]? _anc;
    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) => _codec.Feed(bytes);
    protected override async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        bool ready = (await RefreshAsync(ct).ConfigureAwait(false)).IsSuccess;
        if (ready) StartPolling();
        return ready;
    }
    private Task<BrandPacket?> Query(ushort command, byte[] payload, byte type, Func<byte[], bool> valid, CancellationToken ct) =>
        ExchangeAsync(RaceFrameCodec.Encode(command, payload), p => p.Command == command && p.Type == type && valid(p.Payload), ct);

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            _agentRight = null; // Agent/partner may swap whenever a bud is returned to the case.
            if (earbuds) await Query(0x0CD4, [], 0x5B, p => p is [0, 0 or 1], cancellationToken).ConfigureAwait(false);
            bool battery = false;
            foreach (byte role in earbuds ? new byte[] { 0, 1 } : new byte[] { 0 })
                battery |= await Query(0x0CD6, [role], 0x5D, p => p.Length == 3 && p[0] == 0 && p[1] == role && p[2] <= 100, cancellationToken).ConfigureAwait(false) is not null;
            if (earbuds) await Query(0x000B, [], 0x5D, p => p.Length == 2 && p[0] == 0 && p[1] is > 0 and <= 100, cancellationToken).ConfigureAwait(false);
            if (await ReadAnc(cancellationToken).ConfigureAwait(false) is null)
            {
                _anc = null;
                Store.TryUpdate(DeviceId, s => s with { Profile = new AkgHybridProfile(earbuds), NoiseControl = null });
            }
            return battery && (!earbuds || _agentRight.HasValue) ? CommandResult.Ok : TimedOut;
        }
        finally { Operations.Release(); }
    }
    private Task<BrandPacket?> ReadAnc(CancellationToken ct) => Query(0x0901, [5, 0], 0x5B,
        ValidAnc, ct);
    private static bool ValidAnc(byte[] p) => p.Length is 9 or 15 && p[0] == 5 && p[1] == 0 && p[2] == 0
        && (p.Length == 9 ? p[4] <= 7 : p[4] is <= 4 or >= 9 and <= 11);
    protected override void HandlePacket(BrandPacket packet)
    {
        var p = packet.Payload;
        if (packet.Type == 0x5B && packet.Command == 0x0CD4 && p is [0, 0 or 1]) _agentRight = p[1] == 1;
        else if (packet.Type == 0x5D && packet.Command == 0x0CD6 && p is [0, <= 1, <= 100])
        {
            // Never label agent/partner as left/right without a confirmed channel response.
            if (earbuds && !_agentRight.HasValue) return;
            var component = !earbuds || (p[1] == 0 ? !_agentRight!.Value : _agentRight!.Value) ? BatteryComponent.Left : BatteryComponent.Right;
            Battery(component, p[2]);
        }
        else if (packet.Type == 0x5D && packet.Command == 0x000B && p is [0, > 0 and <= 100]) Battery(BatteryComponent.Case, p[1]);
        else if (packet.Type == 0x5B && packet.Command == 0x0901 && ValidAnc(p))
        {
            _anc = p[3..];
            var mode = (p[3] & 1) == 0 || p[4] == 0 ? NoiseControlMode.Off : p[4] >= (p.Length == 9 ? 5 : 9) ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
            Store.TryUpdate(DeviceId, s => s with { Profile = new AkgHybridProfile(earbuds, true), NoiseControl = Observed.From(new NoiseControlState(mode), Now, StateSource.QueryResponse) });
        }
        else return;
        Touch();
    }
    private void Battery(BatteryComponent component, int value) => Store.TryUpdate(DeviceId, s => s with
    { Battery = s.Battery.With(component, Observed.From(new BatteryReading(value, null), Now, StateSource.QueryResponse)) });

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (target.LevelId is not null || target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency)) return CommandResult.Unsupported("Unsupported ANC mode.");
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            if (await ReadAnc(cancellationToken).ConfigureAwait(false) is null || _anc is null) return CommandResult.Unsupported("ANC module layout has not been confirmed.");
            byte[] desired = (byte[])_anc.Clone(); // Preserve both gains and unrelated flags from this device.
            desired[0] = target.Mode == NoiseControlMode.Off ? (byte)(desired[0] & 0xFE) : (byte)(desired[0] | 1);
            desired[1] = target.Mode switch { NoiseControlMode.Off => 0, NoiseControlMode.Transparency => _anc.Length == 6 ? (byte)6 : (byte)10, _ => 1 };
            var ack = await Query(0x0900, [5, 0, .. desired], 0x5B, p => p is [5, 0, _], cancellationToken).ConfigureAwait(false);
            if (ack is null) return TimedOut;
            if (ack.Payload[2] != 0) return new(CommandStatus.Rejected, "Device rejected ANC change.");
            if (await ReadAnc(cancellationToken).ConfigureAwait(false) is null) return TimedOut;
            return Store.Current.NoiseControl?.Value.Mode == target.Mode ? CommandResult.Ok : new(CommandStatus.Rejected, "ANC readback did not match.");
        }
        finally { Operations.Release(); }
    }
}
