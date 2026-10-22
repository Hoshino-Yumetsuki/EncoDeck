// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Xiaomi;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Xiaomi;

public class XiaomiDeviceControllerTests
{
    private static readonly DeviceIdentity Buds = new("dev-redmi", "Redmi Buds 6", new BluetoothAddress(0x112233445566));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Emulates Xiaomi earbuds behind a <see cref="FakeByteChannel"/>.</summary>
    private sealed class FakeXiaomiBuds : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeXiaomiBuds()
        {
            _loop = Task.Run(RunAsync);
        }

        public FakeByteChannel Channel { get; } = new();

        public byte LowLatency { get; set; }

        /// <summary>When false, writes to config 47 are ignored (device refuses).</summary>
        public bool AcceptWrites { get; set; } = true;

        public List<XiaomiFrame> Received { get; } = [];

        private async Task RunAsync()
        {
            var decoder = new XiaomiFrameDecoder();
            int seen = 0;
            while (!_stop.IsCancellationRequested)
            {
                var written = Channel.Written;
                for (; seen < written.Count; seen++)
                {
                    foreach (var frame in decoder.Feed(written[seen]))
                    {
                        lock (Received) Received.Add(frame);
                        if (Answer(frame) is { } reply) Channel.Receive(reply.Encode());
                    }
                }
                try
                {
                    await Task.Delay(5, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private XiaomiFrame? Answer(XiaomiFrame f)
        {
            switch (f.OpCode)
            {
                case XiaomiProtocol.OpTargetInfo when f.Sequence == XiaomiProtocol.AuthSequence:
                    return f with { Kind = 0x00, Payload = [1, .. XiaomiAuth.GetEncryptedAuthCheckData(f.Payload.AsSpan(1))] };
                case XiaomiProtocol.OpTargetInfo:
                    return f with { Kind = 0x00, Payload = [2, 2, 4, 7, 50, 60, 70] };
                case XiaomiProtocol.OpSetConfig when f.Payload is [3, 0, 47, var value]:
                    if (AcceptWrites) LowLatency = value;
                    return null;
                case XiaomiProtocol.OpGetConfig when f.Payload is [2, 0, 47]:
                    return f with { Kind = 0x00, Payload = [3, 0, 47, LowLatency] };
                default:
                    return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
        }
    }

    private static (DeviceStateStore Store, IDeviceController Controller) Create(FakeXiaomiBuds buds)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = Buds, Profile = XiaomiDeviceProfile.Redmi });
        var context = new ControllerContext(Buds, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (store, XiaomiDeviceProfile.Redmi.CreateController(buds.Channel, context));
    }

    [Fact]
    public async Task Initialize_AuthenticatesAndReadsBattery()
    {
        await using var buds = new FakeXiaomiBuds();
        var (store, controller) = Create(buds);
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));

        var s = store.Current;
        Assert.Equal(50, s.Battery.Left!.Value.Level);
        Assert.Equal(60, s.Battery.Right!.Value.Level);
        Assert.Equal(70, s.Battery.Case!.Value.Level);
        Assert.Equal(StateSource.QueryResponse, s.Battery.Left.Source);
        Assert.False(s.Toggles[DeviceToggle.GamingMode].Value);
        Assert.Equal(CapabilitySupport.Supported, s.Profile!.Capabilities.Get(DeviceCapability.GamingMode).Support);
        Assert.Equal("redmi-earbuds", s.Profile.ModelId);

        List<XiaomiFrame> frames;
        lock (buds.Received) frames = [.. buds.Received];
        var report = Assert.Single(frames, f => f.OpCode == XiaomiProtocol.OpAuthResult);
        Assert.Equal(0x04, report.Kind);
        Assert.Equal([3, 1], report.Payload);
        Assert.True(((XiaomiDeviceController)controller).IsAuthenticated);
    }

    [Fact]
    public async Task SetGamingMode_ConfirmedByReadBack()
    {
        await using var buds = new FakeXiaomiBuds();
        var (store, controller) = Create(buds);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(1, buds.LowLatency);
        var toggle = store.Current.Toggles[DeviceToggle.GamingMode];
        Assert.True(toggle.Value);
        Assert.Equal(StateSource.CommandConfirmation, toggle.Source);
    }

    [Fact]
    public async Task SetGamingMode_RefusedKeepsDeviceValue()
    {
        await using var buds = new FakeXiaomiBuds { AcceptWrites = false };
        var (store, controller) = Create(buds);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.False(store.Current.Toggles[DeviceToggle.GamingMode].Value);
        Assert.NotNull(store.Current.LastError);
    }

    [Fact]
    public async Task NoiseControl_IsUnsupported()
    {
        await using var buds = new FakeXiaomiBuds();
        var (_, controller) = Create(buds);
        await using var __ = controller;
        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
    }
}
