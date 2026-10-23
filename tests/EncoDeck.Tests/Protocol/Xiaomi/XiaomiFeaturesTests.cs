// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Xiaomi;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Xiaomi;

public class XiaomiFeaturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Device : IDuplexByteChannel
    {
        private readonly FakeByteChannel _channel = new();
        public Dictionary<ushort, byte[]> Values { get; } = new() { [11] = [0, 0], [7] = [0], [4] = [0], [29] = [0x96], [47] = [0], [161] = [5, 5, 10, 1] };
        public List<XiaomiFrame> Requests { get; } = [];
        public bool AcceptWrites { get; set; } = true;
        public string Description => "official RCSP fixture";
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _channel.WriteAsync(data, cancellationToken);
            foreach (var f in new XiaomiFrameDecoder().Feed(data.Span))
            {
                lock (Requests) Requests.Add(f);
                byte[]? result = null;
                if (f.OpCode == XiaomiProtocol.OpTargetInfo)
                    result = f.Sequence == XiaomiProtocol.AuthSequence ? [1, .. XiaomiAuth.GetEncryptedAuthCheckData(f.Payload.AsSpan(1))] : [2, 2, 4, 7, 60, 70, 80];
                if (f.OpCode == XiaomiProtocol.OpGetConfig)
                {
                    // Official GetDeviceConfigParam is a raw BE16 list, not CommonConfig TLVs.
                    Assert.Equal(0, f.Payload.Length % 2);
                    var bytes = new List<byte>();
                    for (int i = 0; i < f.Payload.Length; i += 2)
                    {
                        ushort id = (ushort)((f.Payload[i] << 8) | f.Payload[i + 1]);
                        if (Values.TryGetValue(id, out var value)) bytes.AddRange(XiaomiConfigCodec.WritePayload(id, value));
                    }
                    result = bytes.ToArray();
                }
                if (AcceptWrites && f.OpCode == XiaomiProtocol.OpSetConfig && XiaomiConfigCodec.TryParse(f.Payload, out var configs))
                    foreach (var (id, value) in configs)
                        if (id == 161) Values[id][0] = value[0]; else Values[id] = value;
                if (result is not null) _channel.Receive(new XiaomiFrame(4, f.OpCode, 0, [f.Sequence, .. result]).Encode());
            }
        }
        public void Notify(byte[] payload) => _channel.Receive(new XiaomiFrame(0xC4, XiaomiProtocol.OpNotifyConfig, 99, payload).Encode());
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _channel.ReadAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync() => _channel.DisposeAsync();
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(Device d, string name = "Redmi Buds 5 Pro")
    {
        var identity = new DeviceIdentity("xiaomi-test", name, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = XiaomiDeviceProfile.Redmi });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (XiaomiDeviceProfile.Redmi.CreateController(d, context), store);
    }

    [Fact]
    public async Task OfficialReadWriteFixtures_ControlAllReportedFeatures()
    {
        var d = new Device(); var (controller, store) = Create(d); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        foreach (var cap in new[] { DeviceCapability.NoiseCancellation, DeviceCapability.Equalizer, DeviceCapability.SpatialAudio, DeviceCapability.PromptVolume, DeviceCapability.DualDeviceConnection })
            Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(cap).Support);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "nc-2"), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1, 2 }, d.Values[11]);
        Assert.True((await controller.SetEqualizerPresetAsync("xiaomi-5", Ct)).IsSuccess);
        Assert.Equal(new byte[] { 5 }, d.Values[7]);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct)).IsSuccess);
        Assert.True((await controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, Ct)).IsSuccess);
        Assert.Equal(0x9F, d.Values[29][0]); // Preserve preference, surround and reserved bits.
        Assert.True((await controller.SetPromptVolumeAsync(8, Ct)).IsSuccess);
        Assert.Equal(8, store.Current.PromptVolume!.Value);
        Assert.Equal(StateSource.CommandConfirmation, store.Current.PromptVolume.Source);
    }

    [Fact]
    public async Task RefusedWrites_KeepActualReadbackAndReportRejection()
    {
        var d = new Device { AcceptWrites = false }; var (controller, store) = Create(d); await using var session = controller;
        await controller.InitializeAsync(Ct);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("xiaomi-5", Ct)).Status);
        Assert.Equal("xiaomi-0", store.Current.Equalizer!.Value.ActivePresetId);
        Assert.NotNull(store.Current.LastError);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetPromptVolumeAsync(8, Ct)).Status);
        Assert.Equal(5, store.Current.PromptVolume!.Value);
    }

    [Fact]
    public async Task UnknownModel_DoesNotGuessEqPresetOrNoiseLevels()
    {
        var d = new Device(); var (controller, store) = Create(d, "Redmi future unknown"); await using var session = controller;
        await controller.InitializeAsync(Ct);
        Assert.NotEqual(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.Equalizer).Support);
        int before = d.Requests.Count;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("xiaomi-5", Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "nc-2"), Ct)).Status);
        Assert.Equal(before, d.Requests.Count);
    }

    [Fact]
    public async Task Notification_UpdatesStateAndAcknowledgesOriginalSequence()
    {
        var d = new Device(); var (controller, store) = Create(d); await using var session = controller;
        await controller.InitializeAsync(Ct);
        d.Notify([3, 0, 4, 1]);
        XiaomiFrame? ack = null;
        for (int i = 0; i < 100 && ack is null; i++)
        {
            lock (d.Requests) ack = d.Requests.FirstOrDefault(f => f.Kind == 4 && f.OpCode == XiaomiProtocol.OpNotifyConfig);
            if (ack is null) await Task.Delay(10, Ct);
        }
        Assert.NotNull(ack);
        Assert.Equal(0, ack.Sequence); // Response status, followed by the echoed request sequence.
        Assert.Equal(new byte[] { 99 }, ack.Payload);
        Assert.True(store.Current.Toggles[DeviceToggle.DualDeviceConnection].Value);
        Assert.Equal(StateSource.DeviceNotification, store.Current.Toggles[DeviceToggle.DualDeviceConnection].Source);
    }

    [Fact]
    public void ConfigWireVectors_AndMalformedPackets()
    {
        Assert.Equal("000B001D00A1", Convert.ToHexString(XiaomiConfigCodec.ReadPayload(11, 29, 161)));
        Assert.Equal("04000B0102", Convert.ToHexString(XiaomiConfigCodec.WritePayload(11, [1, 2])));
        Assert.False(XiaomiConfigCodec.TryParse([4, 0, 11, 1], out _));
        Assert.False(XiaomiConfigCodec.TryParse([3, 0, 4, 1, 3, 0, 4, 0], out _));
        Assert.Null(XiaomiConfigCodec.NormalizeResponse(new(4, 0xF3, 1, [9, 3, 0, 4, 1])));
        var frame = XiaomiConfigCodec.NormalizeResponse(new(4, 0xF3, 0, [9, 3, 0, 4, 1]));
        Assert.Equal(9, frame!.Sequence);
        Assert.Equal(new byte[] { 3, 0, 4, 1 }, frame.Payload);
    }
}
