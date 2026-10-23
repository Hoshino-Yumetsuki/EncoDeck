// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Brands.Usb;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed class HeadsetAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static DeviceIdentity Identity(string name, DeviceTransport transport = DeviceTransport.Bluetooth) => new("adapter-test", name, new BluetoothAddress(0)) { Transport = transport };

    [Theory]
    [InlineData("AKG N5 HYBRID", "akg-n5hybrid")]
    [InlineData("AKG N5", "akg-n5hybrid")]
    [InlineData("AKG N9 Hybrid", "akg-n9hybrid")]
    [InlineData("MOMENTUM 4", "sennheiser-m4")]
    [InlineData("Sennheiser ACCENTUM Plus", "sennheiser-acp1")]
    [InlineData("MOMENTUM TW4", "sennheiser-tw4")]
    [InlineData("CX Plus True Wireless", "sennheiser-cx400ii")]
    public void ExactModelSelectsControllableProfile(string name, string id)
    {
        var profile = new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(Identity(name));
        Assert.IsAssignableFrom<IControllableProfile>(profile);
        Assert.Equal(id, profile!.ModelId);
        Assert.NotEmpty(profile.ControlServiceUuids);
    }

    [Fact]
    public void RaceAndGaiaFramingSurviveFragmentationNoiseAndRejectWrongVendorsChecksumsAndFlags()
    {
        var race = new RaceFrameCodec();
        var decoded = new List<BrandPacket>();
        var raceBytes = RaceFrameCodec.Encode(0x0CD6, [0, 1, 80], 0x5D);
        foreach (byte b in new byte[] { 1, 2, 3 }.Concat(raceBytes).Concat(raceBytes)) decoded.AddRange(race.Feed([b]));
        Assert.Equal(2, decoded.Count);
        Assert.All(decoded, p => Assert.Equal(new byte[] { 0, 1, 80 }, p.Payload));
        var gaia = new SennheiserFrameCodec();
        var valid = SennheiserFrameCodec.Encode(0x0703, [85]);
        byte[] wrongVendor = (byte[])valid.Clone(); wrongVendor[5] = 0x94;
        byte[] badFlags = (byte[])valid.Clone(); badFlags[2] = 0x80;
        byte[] badChecksum = [.. valid, 0]; badChecksum[2] = 1;
        byte xor = 0; foreach (byte b in badChecksum) xor ^= b;
        badChecksum[^1] = (byte)(xor ^ 1);
        var packets = new List<BrandPacket>();
        foreach (byte b in wrongVendor.Concat(badFlags).Concat(badChecksum).Concat(valid)) packets.AddRange(gaia.Feed([b]));
        Assert.Equal(new byte[] { 85 }, Assert.Single(packets).Payload);
        Assert.Equal(new byte[] { 0xFF, 3, 0, 0, 4, 0x95, 6, 3 }, SennheiserFrameCodec.Encode(0x0603, []));
    }

    [Fact]
    public void SennheiserBleBridgePreservesPduAndHandlesExtendedNotifications()
    {
        var command = SennheiserFrameCodec.Encode(0x0603, []);
        Assert.Equal(new byte[] { 4, 0x95, 6, 3 }, SennheiserGattFraming.UnwrapCommand(command));
        byte[] payload = Enumerable.Repeat((byte)42, 300).ToArray();
        byte[] frame = SennheiserGattFraming.WrapNotification([4, 0x95, 7, 3, .. payload]);
        Assert.Equal(payload, Assert.Single(new SennheiserFrameCodec().Feed(frame)).Payload);
        Assert.Throws<InvalidDataException>(() => SennheiserGattFraming.WrapNotification([0, 0x1D, 7, 3]));
        Assert.Throws<InvalidDataException>(() => SennheiserGattFraming.UnwrapCommand([0xFF, 3, 0, 8, 4, 0x95, 6, 3]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AkgHybridReadsCorrectPhysicalSidesAndConfirmsAncWithoutChangingGains(bool agentRight, bool modern)
    {
        var identity = Identity("AKG N5 Hybrid");
        var profile = new AkgHybridProfile(true);
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new SimulatedChannel(false) { AgentRight = agentRight, ModernAnc = modern };
        await using var controller = profile.CreateController(device, Context(identity, store));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(agentRight ? 80 : 90, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(agentRight ? 90 : 80, store.Current.Battery.Right?.Value.Level);
        Assert.Equal(60, store.Current.Battery.Case?.Value.Level);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(modern ? (byte)10 : (byte)6, device.Filter);
        Assert.Equal(modern ? new byte[] { 0, 0, 0x30, 0, 0x40, 0, 2, 0, 0, 0 } : new byte[] { 0x30, 0, 0x40, 0 }, device.LastAncWrite![4..]);
        device.RejectSet = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
        device.AgentRight = !agentRight;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(agentRight ? 90 : 80, store.Current.Battery.Left?.Value.Level);
    }

    [Fact]
    public async Task SennheiserUsesGaiaV3ResponseBitsAndNeverOffersAncOffOrAcceptsAnErrorAsSuccess()
    {
        var identity = Identity("MOMENTUM 4");
        var profile = (SennheiserProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new SimulatedChannel(true);
        await using var controller = profile.CreateController(device, Context(identity, store));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(85, store.Current.Battery.Left?.Value.Level);
        Assert.Equal("3.38.3", store.Current.Firmware?.Value.Version);
        Assert.DoesNotContain(store.Current.Profile!.NoiseControlOptions, n => n.Mode == NoiseControlMode.Off);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(100, device.Transparency);
        device.RejectSet = true;
        Assert.False((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
    }

    [Theory]
    [InlineData("Speakers (RAPOO VH800)", "rapoo-usb-vh800")]
    [InlineData("Speakers (RAPOO VH800Pro)", "rapoo-usb-vh800-pro")]
    [InlineData("扬声器 (HECATE G4S)", "edifier-usb-g4s")]
    [InlineData("DAREU EH925 ULTRA", "dareu-usb-eh925-ultra")]
    public void UsbProfilesRequireUsbAndKeepSimilarModelsSeparate(string name, string expected)
    {
        var registry = new DeviceProfileRegistry(UsbHeadsetProfiles.Create());
        Assert.Null(registry.Match(Identity(name)));
        var profile = registry.Match(Identity(name, DeviceTransport.Usb));
        Assert.Equal(expected, profile?.ModelId);
        Assert.Empty(profile!.NoiseControlOptions);
        Assert.Empty(profile.ControlServiceUuids);
        Assert.Null(registry.Match(Identity(profile.DisplayName + "not-a-headset", DeviceTransport.Usb)));
    }

    [Theory]
    [InlineData(6, 32, 0, 100, true)]
    [InlineData(133, 100, 0, 100, true)]
    [InlineData(133, 102, 0, 65535, false)]
    [InlineData(65280, 32, 0, 100, false)]
    [InlineData(6, 32, 0, 255, false)]
    public void UsbBatteryAcceptsOnlyDescriptorDeclaredStandardPercentages(int page, int usage, int min, int max, bool expected) =>
        Assert.Equal(expected, UsbHidBattery.IsPercentage((ushort)page, (ushort)usage, min, max));

    [Fact]
    public void RapooHardwareRecognitionRequiresTheExactOfficialDriverVidPid()
    {
        Assert.Equal("rapoo-usb-vh800-pro", UsbHeadsetProfiles.MatchHardware([@"USB\VID_24AE&PID_7205&MI_00"])?.ModelId);
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_24AE&PID_72050"]));
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_24AE&PID_7206"]));
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"BTHENUM\VID_24AE&PID_7205"]));
    }

    private static ControllerContext Context(DeviceIdentity identity, IDeviceStateStore store) => new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);

    private sealed class SimulatedChannel(bool sennheiser) : IDuplexByteChannel
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        public string Description => "Simulated official protocol device";
        public bool AgentRight { get; set; }
        public bool ModernAnc { get; set; }
        public byte Filter { get; private set; } = 1;
        public byte Transparency { get; private set; }
        public bool RejectSet { get; set; }
        public byte[]? LastAncWrite { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            byte[] bytes = data.ToArray();
            if (sennheiser)
            {
                ushort command = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6));
                byte[] payload = command switch
                {
                    0x0603 => [85], 0x0602 => [0], 0x1201 => [0, 3, 0, 38, 0, 3],
                    0x1206 => "M4AEBT"u8.ToArray(), 0x1A03 => [Transparency], _ => [],
                };
                bool error = command == 0x1A02 && RejectSet;
                if (command == 0x1A02 && !error) Transparency = bytes[8];
                _responses.Writer.TryWrite(SennheiserFrameCodec.Encode((ushort)(command | (error ? 0x180 : 0x100)), error ? [1] : payload));
            }
            else
            {
                ushort command = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
                byte type = command is 0x0CD6 or 0x000B ? (byte)0x5D : (byte)0x5B;
                byte[] payload = command switch
                {
                    0x0CD4 => [0, AgentRight ? (byte)1 : (byte)0],
                    0x0CD6 => [0, bytes[6], bytes[6] == 0 ? (byte)90 : (byte)80],
                    0x000B => [0, 60],
                    0x0901 => ModernAnc ? [5, 0, 0, Filter == 0 ? (byte)0 : (byte)1, Filter, 0, 0, 0x30, 0, 0x40, 0, 2, 0, 0, 0]
                        : [5, 0, 0, Filter == 0 ? (byte)0 : (byte)1, Filter, 0x30, 0, 0x40, 0],
                    0x0900 => [5, 0, RejectSet ? (byte)1 : (byte)0],
                    _ => throw new InvalidOperationException($"Unexpected command {command:X4}"),
                };
                if (command == 0x0900) { LastAncWrite = bytes[6..]; if (!RejectSet) Filter = bytes[9]; }
                var response = RaceFrameCodec.Encode(command, payload, type);
                // Split a real RACE header across reads to exercise the session deframer too.
                _responses.Writer.TryWrite(response[..3]); _responses.Writer.TryWrite(response[3..]);
            }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            try { var response = await _responses.Reader.ReadAsync(ct); response.CopyTo(buffer); return response.Length; }
            catch (ChannelClosedException) { return 0; }
        }
        public ValueTask DisposeAsync() { _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
