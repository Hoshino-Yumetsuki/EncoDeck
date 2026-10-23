// SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed partial class MoondropTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("MOONDROP PUDDING", "moondrop-pudding")]
    [InlineData("MOONDROP EDGE2", "moondrop-edge2")]
    [InlineData("MOONDROP SPACE TRAVEL 2 ULTRA", "moondrop-space-travel-2-ultra")]
    [InlineData("MOONDROP SPACE TRAVEL 2", "moondrop-space-travel-2")]
    [InlineData("MOONDROP unknown", "moondrop-generic")]
    public void RegistrySelectsMostSpecificModel(string name, string expected)
    {
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        Assert.Equal(expected, registry.Match(new DeviceIdentity("test", name, new BluetoothAddress(0)))?.ModelId);
    }

    [Fact]
    public void FramedStreamRetainsFragmentsAndChecksChecksum()
    {
        byte[] response = Reply(13, 1, [1, 80, 2, 70], true);
        response[2] = 1;
        byte checksum = 0;
        foreach (byte b in response) checksum ^= b;
        byte[] valid = [.. response, checksum];
        byte[] bad = [.. response, (byte)(checksum ^ 1)];
        var decoder = new GaiaFrameDecoder();
        var frames = new List<GaiaFrame>();
        foreach (byte b in new byte[] { 0x13, 0x37 }.Concat(bad).Concat(valid))
            frames.AddRange(decoder.Feed([b], true));
        Assert.Equal(new byte[] { 1, 80, 2, 70 }, Assert.Single(frames).Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceSessionReadsStateAndConfirmsWrites(bool rfcomm)
    {
        var identity = new DeviceIdentity("moondrop-test", "MOONDROP PUDDING", new BluetoothAddress(0));
        var profile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new PodChannel(rfcomm);
        await using var controller = profile.CreateController(device, new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(98, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(100, store.Current.Battery.Right?.Value.Level);
        Assert.Null(store.Current.Battery.Case);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl?.Value.Mode);
        Assert.Equal(20, store.Current.PromptVolume?.Value);

        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(2, device.Noise);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
        Assert.True((await controller.SetPromptVolumeAsync(80, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0, 80, 7 }, device.Voice);
        Assert.True((await controller.SetPromptVolumeAsync(0, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0, 0, 7 }, device.Voice);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetPromptVolumeAsync(101, Ct)).Status);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct)).IsSuccess);
        Assert.True(device.Dual);
        Assert.False(device.Lhdc);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.HiRes, true, Ct)).IsSuccess);
        Assert.True(device.Lhdc);
        Assert.False(device.Dual);
        Assert.Empty(store.Current.PendingCommands);

        device.RejectNoise = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
        Assert.Empty(store.Current.PendingCommands);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.InEarDetection, true, Ct)).Status);
    }

    [Fact]
    public async Task SingleBatteryAndPerEarUpdatesPreserveOtherComponents()
    {
        var identity = new DeviceIdentity("moondrop-test", "MOONDROP EDGE", new BluetoothAddress(0));
        var profile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new PodChannel(false) { Battery = [0, 65] };
        await using var controller = profile.CreateController(device, new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(65, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(65, store.Current.Battery.Right?.Value.Level);
        device.Battery = [3, 255, 2, 77];
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(65, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(77, store.Current.Battery.Right?.Value.Level);
        device.Battery = [0, 55];
        await controller.RefreshAsync(Ct);
        Assert.Equal(55, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(77, store.Current.Battery.Right?.Value.Level);
    }

    [Fact]
    public void WireCommandsMatchReferenceCapture()
    {
        Assert.Equal(new byte[] { 0xFF, 4, 0, 1, 0, 0x1D, 0x40, 4, 2 }, GaiaFrameCodec.Encode(32, 4, [2], true));
        Assert.Equal(new byte[] { 0, 0x1D, 0x10, 4, 4 }, GaiaFrameCodec.Encode(8, 4, [4], false));
    }

    [Fact]
    public async Task AudioCurationUsesDifferentReadAndWriteMappings()
    {
        var identity = new DeviceIdentity("moondrop-test", "MOONDROP GOLDEN AGES 2", new BluetoothAddress(0));
        var profile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new PodChannel(false) { AncFeature = 8 };
        await using var controller = profile.CreateController(device, new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal((byte)4, device.LastNoiseWrite);
        Assert.Equal(2, device.Noise);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
    }

    [Fact]
    public async Task InferredMappingCannotWriteWithoutExperimentalOptIn()
    {
        var identity = new DeviceIdentity("moondrop-test", "MOONDROP NEKOCAKE", new BluetoothAddress(0));
        var profile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new PodChannel(false) { AncFeature = 8 };
        await using var controller = profile.CreateController(device, new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.HiRes, true, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetPromptVolumeAsync(50, Ct)).Status);
        Assert.Null(device.LastNoiseWrite);
    }

    private static byte[] Reply(int feature, int command, byte[] payload, bool rfcomm)
    {
        byte[] data = GaiaFrameCodec.Encode(feature, command, payload, rfcomm);
        data[(rfcomm ? 4 : 0) + 2] |= 1; // QTiL RESPONSE = 2 << 7.
        return data;
    }

    /// <summary>Exercises the real controller through its byte channel, including fragmented SPP reads.</summary>
    private sealed class PodChannel(bool rfcomm, bool rawRfcomm = false) : IDuplexByteChannel
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly GaiaFrameDecoder _decoder = new();
        public string Description => rfcomm ? "RFCOMM test" : "GATT test";
        public byte[] Battery { get; set; } = [1, 98, 2, 100, 3, 255];
        public byte[] Voice { get; set; } = [0, 20, 7];
        public byte Noise { get; private set; }
        public bool Dual { get; private set; }
        public bool Lhdc { get; private set; } = true;
        public bool RejectNoise { get; set; }
        public byte AncFeature { get; set; } = 32;
        public byte? LastNoiseWrite { get; private set; }
        public byte[] Touch { get; set; } = [0x11, 0x23, 0xEF, 0x77, 0x89];
        public byte[]? LastTouchWrite { get; private set; }
        public byte Gain { get; private set; }
        public byte? LastGainWrite { get; private set; }
        public bool Led { get; private set; } = true;
        public bool RejectGain { get; set; }
        public bool RejectTouchOtherSlot { get; set; }
        public bool RejectVoiceIndex { get; set; }
        public bool SuppressGainRead { get; set; }
        public HashSet<int> OmittedFeatures { get; } = [];
        public int SettingWrites { get; private set; }
        public int FramedProbes { get; private set; }
        public int RawProbes { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            bool framed = data.Span[0] == 0xFF;
            var pdu = data.Span[(framed ? 4 : 0)..];
            if (pdu[1] == 0x0A)
            {
                if (framed) FramedProbes++; else RawProbes++;
                if (framed == !rawRfcomm)
                    _responses.Writer.TryWrite(framed ? [0xFF, 4, 0, 4, 0, 0x0A, 0x83, 0, 0, 3, 3, 0] : [0, 0x0A, 0x83, 0, 0, 3, 3, 0]);
                return ValueTask.CompletedTask;
            }
            var request = Assert.Single(_decoder.Feed(data.Span, rfcomm && !rawRfcomm));
            if (request.Feature == 15 && request.Command == 1 && SuppressGainRead) return ValueTask.CompletedTask;
            if (request.Payload.Length > 0 && request.Feature is 14 or 15 or 19 or 22) SettingWrites++;
            byte[] payload = (request.Feature, request.Command) switch
            {
                (0, 1) => Features(),
                (13, 0) => [0, 1, 2, 3],
                (13, 1) => Battery,
                (8 or 32, 3) => [Noise],
                (8 or 32, 4) => SetNoise(request.Payload),
                (14, 1) => Voice,
                (14, 2) => SetVoice(request.Payload),
                (15, 1) => [Gain],
                (15, 2) => SetGain(request.Payload),
                (19, 1) => [Led ? (byte)1 : (byte)0],
                (19, 2) => SetLed(request.Payload),
                (22, 2) => Touch,
                (22, 3) => SetTouch(request.Payload),
                (16, 5) => [Lhdc ? (byte)1 : (byte)0],
                (16, 6) => SetLhdc(request.Payload),
                (20, 1) => [Dual ? (byte)1 : (byte)0],
                (20, 2) => SetDual(request.Payload),
                _ => [],
            };
            byte[] reply = Reply(request.Feature, request.Command, payload, rfcomm && !rawRfcomm);
            if (rfcomm)
            {
                _responses.Writer.TryWrite(reply[..3]);
                _responses.Writer.TryWrite(reply[3..]);
            }
            else _responses.Writer.TryWrite(reply);
            return ValueTask.CompletedTask;
        }

        private byte[] SetNoise(byte[] payload)
        {
            LastNoiseWrite = payload[0];
            if (!RejectNoise) Noise = AncFeature == 8 ? (byte)(payload[0] switch { 1 => 0, 2 => 1, 4 => 2, 3 => 3, _ => 255 }) : payload[0];
            return [];
        }
        private byte[] SetVoice(byte[] payload) { Voice = (byte[])payload.Clone(); if (RejectVoiceIndex) Voice[2]++; return Voice; }
        private byte[] SetLhdc(byte[] payload) { Lhdc = payload[0] == 1; return payload; }
        private byte[] SetDual(byte[] payload) { Dual = payload[0] == 1; return payload; }
        private byte[] Features() => [0, .. new[] { 0, 13, 14, 15, 16, 19, 20, 22, (int)AncFeature }.Where(f => !OmittedFeatures.Contains(f)).SelectMany(f => new byte[] { (byte)f, 1 })];
        private byte[] SetGain(byte[] payload) { LastGainWrite = payload[0]; if (!RejectGain) Gain = payload[0]; return []; }
        private byte[] SetLed(byte[] payload) { Led = payload[0] == 1; return []; }
        private byte[] SetTouch(byte[] payload) { LastTouchWrite = (byte[])payload.Clone(); Touch = (byte[])payload.Clone(); if (RejectTouchOtherSlot) Touch[0] ^= 1; return Touch; }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await _responses.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var data = await _responses.Reader.ReadAsync(cancellationToken);
            data.CopyTo(buffer.Span);
            return data.Length;
        }

        public ValueTask DisposeAsync() { _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
