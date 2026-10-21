// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Sdp;
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Protocol;

public class CapabilityBitmapTests
{
    // Handshake response captured from a real Enco X4.
    private static readonly OppoFrame X4Handshake = new(0x8100, 0x01, Convert.FromHexString("00FF775AEEEE8FA65F170000000000000000"[..34]));

    [Fact]
    public void X4Bitmap_AllowsVerifiedCommands()
    {
        Assert.True(OppoCapabilityBitmap.TryParse(X4Handshake, out var caps));
        Assert.True(caps!.Allows(OppoCommand.QueryBattery));
        Assert.True(caps.Allows(OppoCommand.SetNoiseControl));   // bit 8
        Assert.True(caps.Allows(OppoCommand.QueryNoiseControl)); // bit 8
        Assert.True(caps.Allows(OppoCommand.SetEqualizer));      // bit 10
        Assert.True(caps.Allows(OppoCommand.QueryEqualizerList)); // bit 34
        Assert.True(caps.Allows(OppoCommand.SetSpatialAudio));   // bit 47
        Assert.True(caps.Allows(OppoCommand.SubscribeEvents));   // bit 22
        Assert.False(caps.Allows(0x0407));                       // bit 11 clear on X4
        Assert.True(caps.Allows(OppoCommand.Handshake));         // always allowed
    }

    [Fact]
    public void EmptyBitmap_BlocksGatedCommands()
    {
        var caps = new OppoCapabilityBitmap([0x00]);
        Assert.False(caps.Allows(OppoCommand.SetNoiseControl));
        Assert.False(caps.Allows(OppoCommand.QueryFirmware));
        Assert.True(caps.Allows(OppoCommand.QueryProductId));
        Assert.True(caps.Allows(OppoCommand.QueryFeatureStatus));
    }

    [Fact]
    public void NonZeroStatus_IsNotParsed()
    {
        Assert.False(OppoCapabilityBitmap.TryParse(new OppoFrame(0x8100, 1, [0x01, 0xFF]), out _));
    }
}

public class SdpTests
{
    [Fact]
    public void ParsesRfcommChannelAndServiceName()
    {
        // ProtocolDescriptorList: seq{ seq{UUID16 0x0100 L2CAP}, seq{UUID16 0x0003 RFCOMM, uint8 15} }
        byte[] protocols = [0x35, 0x0C, 0x35, 0x03, 0x19, 0x01, 0x00, 0x35, 0x05, 0x19, 0x00, 0x03, 0x08, 0x0F];
        // ServiceClassIDList: seq{ UUID128 0000079A-D102-11E1-9B23-00025B00A5A5 }
        byte[] classes = [0x35, 0x11, 0x1C, .. new Guid("0000079A-D102-11E1-9B23-00025B00A5A5").ToByteArray(bigEndian: true)];
        byte[] name = [0x25, 0x04, (byte)'E', (byte)'L', (byte)'N', (byte)'K'];

        var info = SdpRecordInfo.FromRawAttributes(new Dictionary<uint, byte[]>
        {
            [SdpRecordInfo.ProtocolDescriptorListAttribute] = protocols,
            [SdpRecordInfo.ServiceClassIdListAttribute] = classes,
            [SdpRecordInfo.ServiceNameAttribute] = name,
        });

        Assert.Equal(15, info.RfcommChannel);
        Assert.Equal("ELNK", info.ServiceName);
        Assert.Equal(OppoServiceUuids.Secondary, Assert.Single(info.ServiceClassIds));
    }

    [Fact]
    public void TruncatedElement_IsRejected()
    {
        Assert.False(SdpDataElement.TryParse(new byte[] { 0x35, 0x10, 0x19 }, out _));
    }
}
