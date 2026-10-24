// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Bluetooth;

/// <summary>LE Audio detection and pairing inputs, from the Enco X4 (2026-10-24, values anonymised).</summary>
public class LeAudioTests
{
    private const ulong Primary = 0x112233445566;

    [Fact]
    public void X4Bitmap_AllowsTheLeBrSwitchQuery()
    {
        // The X4's handshake bitmap: bit 64 gates 0x0136 / 0x0433 like the official client.
        Assert.True(OppoCapabilityBitmap.TryParse(new OppoFrame(0x8100, 0, [0x00, .. Convert.FromHexString("FF775AEEEE8FA65F1700000000000000")]), out var bitmap));
        Assert.True(bitmap!.Allows(OppoCommand.QueryLeBrSwitch));
        Assert.False(new OppoCapabilityBitmap(new byte[16]).Allows(OppoCommand.QueryLeBrSwitch));
    }

    [Fact]
    public void LeBrSwitchResponse_DecodesTheOfficialBits()
    {
        // X4 answer before and after setting the switch: 00 02 (owner) and 00 03 (on, owner).
        Assert.True(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 2, [0x00, 0x02]), out var off));
        Assert.Equal(new(false, true, false), off);
        Assert.True(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 4, [0x00, 0x07]), out var all));
        Assert.Equal(new(true, true, true), all);
        Assert.False(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 2, [0x01, 0x02]), out _));
        Assert.False(OppoFeatureParsers.TryParseLeAudioSwitch(new OppoFrame(0x8136, 2, [0x00]), out _));
    }

    [Fact]
    public void ScanResponse_NamesTheSecondaryEarbud()
    {
        // Manufacturer 0xBF0D payload of the primary's scan response: [00 00][address LE][..].
        byte[] data = Convert.FromHexString("0000B2A144332211" + "82008200");
        Assert.True(LeAudioPairing.TryParseSecondaryAddress(Primary, data, out ulong secondary));
        Assert.Equal(0x11223344A1B2UL, secondary);
    }

    [Theory]
    [InlineData("000066554433221182008200")] // the primary itself
    [InlineData("0000B2A144CCBBAA")] // another vendor's OUI
    [InlineData("0000B2A1443322")] // too short
    public void ScanResponse_RejectsAddressesThatAreNotTheOtherEarbud(string hex)
    {
        Assert.False(LeAudioPairing.TryParseSecondaryAddress(Primary, Convert.FromHexString(hex), out _));
    }
}
