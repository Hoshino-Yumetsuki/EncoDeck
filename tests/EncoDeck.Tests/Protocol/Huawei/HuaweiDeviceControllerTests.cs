// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Huawei;

public class HuaweiDeviceControllerTests
{
    private static readonly DeviceIdentity Pro3 = new("dev-pro3", "HUAWEI FreeBuds Pro 3", new BluetoothAddress(0x112233445566));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Emulates HUAWEI earbuds on top of <see cref="FakeByteChannel"/> (whose responder only speaks OPPO).</summary>
    private sealed class FakeHuaweiDevice : IDuplexByteChannel
    {
        private readonly LegacyHuaweiFrameCodec _codec = new();

        public FakeByteChannel Inner { get; } = new();

        public bool Silent { get; set; }

        public bool IgnoreAncWrites { get; set; }

        public byte[] AncState { get; set; } = [0x00, 0x00]; // [sub, mode]

        public List<HuaweiFrame> Received { get; } = [];

        public string Description => "fake huawei";

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await Inner.WriteAsync(data, cancellationToken);
            foreach (var frame in new LegacyHuaweiFrameCodec().Feed(data.Span))
            {
                lock (Received) Received.Add(frame);
                if (Silent) continue;
                switch (frame.Command)
                {
                    case HuaweiCommands.Battery:
                        Reply(HuaweiCommands.Battery, [0x02, 0x03, 0x50, 0x46, 0x20, 0x03, 0x03, 0x01, 0x00, 0x01]);
                        break;
                    case HuaweiCommands.QueryAnc:
                        Reply(HuaweiCommands.QueryAnc, [0x01, 0x02, .. AncState]);
                        break;
                    case HuaweiCommands.QueryWearDetection:
                        Reply(HuaweiCommands.QueryWearDetection, [0x01, 0x01, 0x01]);
                        break;
                    case HuaweiCommands.QueryEqualizer:
                        Reply(HuaweiCommands.QueryEqualizer, [0x01, 0x01, 0x01, 0x02, 0x01, 0x05]);
                        break;
                    case HuaweiCommands.LowLatency:
                        Reply(HuaweiCommands.LowLatency, [0x02, 0x01, 0x00]);
                        break;
                    case HuaweiCommands.QueryDualConnect:
                        Reply(HuaweiCommands.QueryDualConnect, [0x01, 0x01, 0x01]);
                        break;
                    case HuaweiCommands.SetAnc when !IgnoreAncWrites:
                        AncState = [frame.Payload[3], frame.Payload[2]];
                        break;
                }
            }
        }

        private void Reply(ushort command, byte[] payload) => Inner.Receive(_codec.Encode(command, payload));

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Inner.ReadAsync(buffer, cancellationToken);

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(FakeHuaweiDevice device)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = Pro3, Connection = DeviceConnectionState.ControlConnecting });
        var profile = HuaweiProfiles.CreateBuiltIn().Cast<HuaweiDeviceProfile>().Single(p => p.MatchScore(Pro3) == 100);
        var context = new ControllerContext(Pro3, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (profile.CreateController(device, context), store);
    }

    [Fact]
    public async Task Initialize_ReadsBatteryAndNoiseIntoStore()
    {
        var device = new FakeHuaweiDevice { AncState = [0x02, 0x01] };
        var (controller, store) = Create(device);
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));

        var s = store.Current;
        Assert.Equal(new BatteryReading(80, true), s.Battery.Left!.Value);
        Assert.Equal(new BatteryReading(70, false), s.Battery.Right!.Value);
        Assert.Equal(new BatteryReading(32, true), s.Battery.Case!.Value);
        Assert.Equal(StateSource.QueryResponse, s.Battery.Left.Source);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "ultra"), s.NoiseControl!.Value);
        Assert.NotNull(s.LastResponseAt);
        Assert.Equal("5A0009000108010002000300FBB9", Convert.ToHexString(device.Inner.Written[0]));
    }

    [Fact]
    public async Task Initialize_FailsWhenDeviceIsSilent()
    {
        var device = new FakeHuaweiDevice { Silent = true };
        var (controller, _) = Create(device);
        await using var _c = controller;
        Assert.False(await controller.InitializeAsync(Ct));
    }

    [Fact]
    public async Task SetNoiseControl_IsConfirmedByReadBack()
    {
        var device = new FakeHuaweiDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        await controller.InitializeAsync(Ct);

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation, "dynamic"), Ct);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains(device.Received, f => f.Command == HuaweiCommands.SetAnc && f.Payload.SequenceEqual(new byte[] { 0x01, 0x02, 0x01, 0x03 }));
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "dynamic"), store.Current.NoiseControl!.Value);
        Assert.True(store.Current.NoiseControl.IsConfirmed);
        Assert.Empty(store.Current.PendingCommands);
    }

    [Fact]
    public async Task SetNoiseControl_RollsBackWhenDeviceKeepsOldValue()
    {
        var device = new FakeHuaweiDevice { IgnoreAncWrites = true };
        var (controller, store) = Create(device);
        await using var _ = controller;
        await controller.InitializeAsync(Ct);

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), store.Current.NoiseControl!.Value);
        Assert.Empty(store.Current.PendingCommands);
    }

    [Fact]
    public async Task UnsupportedFeature_IsReportedAsUnsupported()
    {
        var device = new FakeHuaweiDevice();
        var (controller, _) = Create(device);
        await using var _c = controller;
        await controller.InitializeAsync(Ct);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.SpatialAudio, true, Ct)).Status);
    }
}
