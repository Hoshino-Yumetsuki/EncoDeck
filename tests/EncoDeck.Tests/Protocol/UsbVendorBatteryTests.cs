// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Brands.Usb;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Tests.Protocol;

public sealed class UsbVendorBatteryTests
{
    [Theory]
    [InlineData(0x3837, 0x6045, 0xFF90, true)]
    [InlineData(0x3837, 0x6045, 0xFF00, false)]
    [InlineData(0x3837, 0x6046, 0xFF90, false)]
    [InlineData(0x1532, 0x0528, 0xFF00, true)]
    [InlineData(0x1532, 0x0555, 0xFF00, true)]
    [InlineData(0x1532, 0x0556, 0xFF00, false)]
    [InlineData(0x1532, 0x052E, 0xFF00, false)]
    public void VendorQueriesAreGatedByExactReceiverAndCollection(int vendor, int product, int page, bool expected)
    {
        Assert.Equal(expected, UsbVendorBatteryProtocol.IsCollection((ushort)vendor, (ushort)product, (ushort)page, 1));
        Assert.False(UsbVendorBatteryProtocol.IsCollection((ushort)vendor, (ushort)product, (ushort)page, 2));
    }

    [Fact]
    public void MchoseIgnoresHeartbeatAndOtherFieldsAndInvalidPercentages()
    {
        Assert.Equal(new byte[] { 0x55, 0x65, 1, 0 }, UsbVendorBatteryProtocol.Query(0x3837, 0x6045)[..4]);
        var frame = new byte[64]; frame[0] = 0x55; frame[1] = 0x65; frame[2] = 73; frame[3] = 2;
        Assert.Equal(73, UsbVendorBatteryProtocol.Parse(0x3837, 0x6045, frame));
        Assert.Null(UsbVendorBatteryProtocol.Parse(0x3837, 0x6046, frame));
        Assert.Null(UsbVendorBatteryProtocol.Parse(0x3837, 0x6045, frame[..4]));
        frame[3] = 1; Assert.Null(UsbVendorBatteryProtocol.Parse(0x3837, 0x6045, frame));
        frame[3] = 2; frame[2] = 255; Assert.Null(UsbVendorBatteryProtocol.Parse(0x3837, 0x6045, frame));
    }

    [Fact]
    public void Blackshark2020RejectsCachedOfflineAndUnreliableChargingLevels()
    {
        var frame = Convert.FromHexString("ff0f05fe12041f08050305010ec050").Concat(new byte[49]).ToArray();
        Assert.Equal(80, UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        frame[11] = 9; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        frame[11] = 6; frame[14] = 100; Assert.Equal(100, UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        frame[1] = 1; frame[2] = 0; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        Assert.Equal(Convert.FromHexString("ff0a00fd0412f10205"), UsbVendorBatteryProtocol.Query(0x1532, 0x0528)[..9]);
    }

    [Fact]
    public void Blackshark2023MatchesBatteryAckRatherThanRemoteOrChargingAck()
    {
        var frame = new byte[64]; frame[0] = 2; frame[12] = 0x21; frame[13] = 1; frame[14] = 1; frame[15] = 65;
        Assert.Equal(65, UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[12] = 0x2A; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[12] = 0x21; frame[14] = 49; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[14] = 1; frame[15] = 101; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        Assert.Equal(1, UsbVendorBatteryProtocol.Remote(true)[11]);
        Assert.Equal(0, UsbVendorBatteryProtocol.Remote(false)[11]);
        Assert.Throws<ArgumentException>(() => UsbVendorBatteryProtocol.Query(0x1532, 0x0556));
    }

    [Theory]
    [InlineData("Speakers (MCHOSE V9 Pro)", "mchose-usb-v9-pro")]
    [InlineData("MCHOSE X9", "mchose-usb-x9")]
    [InlineData("Razer Kraken V4 Pro", "razer-usb-kraken-v4-pro")]
    [InlineData("Razer BlackShark V2 HyperSpeed", "razer-usb-blackshark-v2-hyperspeed")]
    public void SpecificModelsWinOverShorterNames(string name, string id)
    {
        var registry = new DeviceProfileRegistry(UsbHeadsetProfiles.Create());
        var identity = new DeviceIdentity("test", name, new BluetoothAddress(0)) { Transport = DeviceTransport.Usb };
        Assert.Equal(id, registry.Match(identity)?.ModelId);
        Assert.Null(registry.Match(identity with { Transport = DeviceTransport.Bluetooth }));
    }

    [Fact]
    public void GenericBlacksharkNameRequiresHardwareToDistinguishYear()
    {
        var registry = new DeviceProfileRegistry(UsbHeadsetProfiles.Create());
        foreach (var name in new[] { "Razer Kraken V4 X", "MCHOSE X9 Pro", "Razer Barracuda X Chroma", "Razer BlackShark V2 Pro" })
            Assert.Null(registry.Match(new DeviceIdentity("test", name, new BluetoothAddress(0)) { Transport = DeviceTransport.Usb }));
        Assert.Equal("razer-usb-blackshark-v2-pro-2023", UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_0555&MI_03"])?.ModelId);
        Assert.Equal("mchose-usb-x9", UsbHeadsetProfiles.MatchHardware([@"USB\VID_3837&PID_6045"])?.ModelId);
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_05550"]));
    }
}
