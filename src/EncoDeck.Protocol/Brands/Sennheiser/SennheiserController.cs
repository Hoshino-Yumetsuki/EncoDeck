// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Sennheiser;

internal sealed class SennheiserController(IDuplexByteChannel channel, ControllerContext context, SennheiserProfile profile, bool earbuds, bool slider)
    : RequestReplyController(channel, context)
{
    private readonly SennheiserFrameCodec _codec = new();
    private bool _noiseReady;
    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) => _codec.Feed(bytes);
    private async Task<BrandPacket?> Query(ushort command, byte[] payload, Func<byte[], bool> valid, CancellationToken ct)
    {
        var reply = await ExchangeAsync(SennheiserFrameCodec.Encode(command, payload), p =>
            p.Command == (command | 0x180) || (p.Command == (command | 0x100) && valid(p.Payload)), ct).ConfigureAwait(false);
        return reply?.Type == 2 ? reply : null;
    }
    protected override async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        if (!(await RefreshAsync(ct).ConfigureAwait(false)).IsSuccess) return false;
        foreach (byte feature in slider ? new byte[] { 3, 13 } : new byte[] { 3 })
            await Query(0x0007, [feature], p => p.Length == 0, ct).ConfigureAwait(false);
        StartPolling();
        return true;
    }
    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            var battery = await Query(0x0603, [], ValidBattery, cancellationToken).ConfigureAwait(false);
            if (battery is null) return TimedOut;
            await Query(0x0602, [], p => p.Length is >= 1 and <= 3 && p.All(v => v <= 2 || v == 255), cancellationToken).ConfigureAwait(false);
            await Query(0x1201, [], p => p.Length == 6, cancellationToken).ConfigureAwait(false);
            await Query(0x1206, [], p => p.Length is > 0 and <= 100, cancellationToken).ConfigureAwait(false);
            if (slider) _noiseReady = await Query(0x1A03, [], p => p is [<= 100], cancellationToken).ConfigureAwait(false) is not null;
            Store.TryUpdate(DeviceId, s => s with { Profile = profile.WithNoise(_noiseReady) });
            return CommandResult.Ok;
        }
        finally { Operations.Release(); }
    }
    private bool ValidBattery(byte[] p) => p.Length >= (earbuds ? 2 : 1) && p.Length <= (earbuds ? 3 : 1)
        && p.All(v => v <= 100 || v == 255) && p.Any(v => v <= 100);
    protected override void HandlePacket(BrandPacket packet)
    {
        if (packet.Type is not (1 or 2)) return;
        ushort command = (ushort)(packet.Command & ~0x180);
        byte[] p = packet.Payload;
        if (command == 0x0603 && ValidBattery(p))
        {
            for (int i = 0; i < p.Length; i++) if (p[i] <= 100)
            {
                int index = i;
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With((BatteryComponent)index,
                    Observed.From(new BatteryReading(p[index], s.Battery.Get((BatteryComponent)index)?.Value.IsCharging), Now, StateSource.QueryResponse)) });
            }
        }
        else if (command == 0x0602 && p.Length is >= 1 and <= 3 && p.All(v => v <= 2 || v == 255))
        {
            for (int i = 0; i < p.Length; i++) if (p[i] <= 2)
            {
                int index = i;
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With((BatteryComponent)index,
                    Observed.From(new BatteryReading(s.Battery.Get((BatteryComponent)index)?.Value.Level, p[index] == 1), Now, StateSource.QueryResponse)) });
            }
        }
        else if (slider && command == 0x1A03 && p is [<= 100])
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(p[0] == 0 ? NoiseControlMode.NoiseCancellation : NoiseControlMode.Transparency,
                p[0] is 0 or 100 ? null : "percent:" + p[0]), Now, StateSource.QueryResponse) });
        else if (command == 0x1201 && p.Length == 6)
        {
            string firmware = $"{BinaryPrimitives.ReadUInt16BigEndian(p)}.{BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2))}.{BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4))}";
            Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From((s.Firmware?.Value ?? FirmwareInfo.Empty) with { Version = firmware }, Now, StateSource.QueryResponse) });
        }
        else if (command == 0x1206 && p.Length is > 0 and <= 100 && p.All(b => b is >= 32 and <= 126))
            Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From((s.Firmware?.Value ?? FirmwareInfo.Empty) with { ModelCode = Encoding.UTF8.GetString(p) }, Now, StateSource.QueryResponse) });
        else return;
        Touch();
    }
    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!slider || !_noiseReady || target.LevelId is not null || target.Mode is not (NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            return CommandResult.Unsupported("This model exposes confirmed ANC/transparency endpoints only; ANC off is not inferred.");
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte level = target.Mode == NoiseControlMode.Transparency ? (byte)100 : (byte)0;
            if (await Query(0x1A02, [level], p => p.Length == 0, cancellationToken).ConfigureAwait(false) is null) return TimedOut;
            var readback = await Query(0x1A03, [], p => p is [<= 100], cancellationToken).ConfigureAwait(false);
            return readback is null ? TimedOut : readback.Payload[0] == level ? CommandResult.Ok : new(CommandStatus.Rejected, "Transparency readback did not match.");
        }
        finally { Operations.Release(); }
    }
}
