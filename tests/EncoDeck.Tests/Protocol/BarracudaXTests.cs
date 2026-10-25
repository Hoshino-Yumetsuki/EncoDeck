// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Protocol.Brands.Usb;

namespace EncoDeck.Tests.Protocol;

public sealed class BarracudaXTests
{
    [Fact]
    public void OnlyDocumentedReceiverCollectionsReceiveQueries()
    {
        Assert.True(BarracudaXProtocol.IsCollection(0x1532, 0x0552, 0xFF00, 1));
        Assert.True(BarracudaXProtocol.IsCollection(0x1532, 0x0550, 0xFF00, 1));
        Assert.False(BarracudaXProtocol.IsCollection(0x1532, 0x0551, 0xFF00, 1));
        Assert.False(BarracudaXProtocol.IsCollection(0x1532, 0x0550, 0xFF00, 2));
        Assert.False(BarracudaXProtocol.IsCollection(0x1532, 0x0552, 0x000C, 1));
        Assert.False(BarracudaXProtocol.IsCollection(0x1532, 0x0552, 0xFF00, 2));
        Assert.False(BarracudaXProtocol.IsCollection(0x1533, 0x0552, 0xFF00, 1));
        Assert.Equal("razer-usb-barracuda-x", UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_0552&MI_03"])?.ModelId);
        Assert.Equal("razer-usb-barracuda-x", UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_0550&MI_03"])?.ModelId);
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_05520"]));
    }

    [Fact]
    public void ReplacementEnvelopeMatchesOfficialSdkCrcAndReportId()
    {
        Assert.Equal(Convert.FromHexString("028401DE520100000000000000000000"), BarracudaXProtocol.ReplacementHandshake()[..16]);
        Assert.Equal(Convert.FromHexString("0280064B3450410E2101E30000000000"), BarracudaXProtocol.ReplacementQuery(0x21, 0xE3)[..16]);
        Assert.Equal(Convert.FromHexString("02800856175041082103210000000000"), BarracudaXProtocol.ReplacementQuery(0x21, 0x21)[..16]);
        Assert.Throws<ArgumentOutOfRangeException>(() => BarracudaXProtocol.ReplacementQuery(1, 0xF0));
    }

    [Theory]
    [InlineData(0xE3, false, "01800650410E2101E3")]
    [InlineData(0xE0, false, "01800650410E2101E0")]
    [InlineData(0xE1, true, "01800750410E2102E101")]
    [InlineData(0xE1, false, "01800750410E2102E100")]
    [InlineData(0x21, false, "0180085041082103210000")]
    [InlineData(0x2A, false, "01800850410821032A0000")]
    public void ReadQueriesMatchDocumentedWireFormat(int command, bool remote, string hex)
    {
        byte[] frame = BarracudaXProtocol.Query(0x21, (byte)command, remote);
        byte[] prefix = Convert.FromHexString(hex);
        Assert.Equal(64, frame.Length);
        Assert.Equal(prefix, frame[..prefix.Length]);
        Assert.All(frame[prefix.Length..], b => Assert.Equal(0, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarracudaXProtocol.Query(1, 0xF0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarracudaXProtocol.Query(0, 0xE3));
    }

    [Fact]
    public void AckMustMatchSequenceFamilyAndSuccess()
    {
        byte[] ack = Convert.FromHexString("504901B10000000003000EA100");
        Assert.True(BarracudaXProtocol.IsAck(ack, 0x21));
        Assert.False(BarracudaXProtocol.IsAck(ack, 0x22));
        ack[12] = 1; Assert.False(BarracudaXProtocol.IsAck(ack, 0x21));
        ack[12] = 0; ack[10] = 6; Assert.False(BarracudaXProtocol.IsAck(ack, 0x21));
    }

    [Fact]
    public void LinkUsesE3FieldAndNeverBatteryOrAck()
    {
        byte[] link = Convert.FromHexString("50490EF5000000000200E301");
        Assert.True(BarracudaXProtocol.Link(link));
        link[11] = 0; Assert.False(BarracudaXProtocol.Link(link));
        link[11] = 2; Assert.Null(BarracudaXProtocol.Link(link));
        link[11] = 1; link[10] = 0xE6; Assert.Null(BarracudaXProtocol.Link(link));
        Assert.Null(BarracudaXProtocol.Link(Convert.FromHexString("504908F500000000040021010164")));
        Assert.False(DeviceConnectionState.UsbReceiverConnected.IsLinkUp());
        Assert.True(DeviceConnectionState.UsbHeadsetConnected.IsLinkUp());
        Assert.False(DeviceConnectionState.UsbHeadsetConnected.CanSendCommands());
    }

    [Fact]
    public void RouteDoesNotInferConnectionOrAcceptUnknownModes()
    {
        byte[] route = Convert.FromHexString("50490EF5000000000200E000");
        Assert.Equal(0, BarracudaXProtocol.Route(route));
        Assert.Null(BarracudaXProtocol.Link(route));
        route[11] = 1; Assert.Equal(1, BarracudaXProtocol.Route(route));
        route[11] = 2; Assert.Null(BarracudaXProtocol.Route(route));
    }

    [Fact]
    public void TelemetryRejectsNotificationsAndInvalidValues()
    {
        byte[] battery = Convert.FromHexString("504908F500000000040021010149");
        Assert.Equal(73, BarracudaXProtocol.Value(battery, 0x21));
        Assert.Null(BarracudaXProtocol.Value(battery, 0x2A));
        battery[13] = 0; Assert.Equal(0, BarracudaXProtocol.Value(battery, 0x21));
        battery[13] = 101; Assert.Null(BarracudaXProtocol.Value(battery, 0x21));
        battery[13] = 73; battery[11] = 2; Assert.Null(BarracudaXProtocol.Value(battery, 0x21));
        byte[] cable = Convert.FromHexString("504908F50000000004002A010101");
        Assert.Equal(1, BarracudaXProtocol.Value(cable, 0x2A));
        cable[13] = 0; Assert.Equal(0, BarracudaXProtocol.Value(cable, 0x2A));
        cable[13] = 2; Assert.Null(BarracudaXProtocol.Value(cable, 0x2A));
    }

    [Fact]
    public void DecoderReassemblesFragmentsAndSplitsCoalescedMessages()
    {
        var decoder = new BarracudaXDecoder();
        byte[] link = Convert.FromHexString("50490EF5000000000200E301");
        byte[] battery = Convert.FromHexString("504908F600000000040021010149");
        Assert.Empty(decoder.Feed(Report(link[..7])));
        var frames = decoder.Feed(Report([.. link[7..], .. battery]));
        Assert.Equal(2, frames.Count);
        Assert.Equal(link, frames[0]); Assert.Equal(battery, frames[1]);
        Assert.Empty(decoder.Feed(new byte[64]));
        Assert.Empty(decoder.Feed([1, 0x80, 64]));
        Assert.Empty(decoder.Feed(Report(Convert.FromHexString("5049080000000000FFFF"))));
        Assert.Equal(link, Assert.Single(decoder.Feed(Report(link))));
    }

    private static byte[] Report(byte[] payload)
    {
        var report = new byte[64]; report[0] = 1; report[1] = 0x80; report[2] = (byte)payload.Length;
        payload.CopyTo(report, 3); return report;
    }
}
