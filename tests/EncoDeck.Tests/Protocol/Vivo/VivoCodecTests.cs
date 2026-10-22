// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Vivo;

namespace EncoDeck.Tests.Protocol.Vivo;

public class VivoCodecTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Theory]
    // Golden frames from the reference captures (handshake / battery always v4, noise set on a v3 model).
    [InlineData(0x0300, "", 3, "FF 04 00 00 00 0A 03 00")]
    [InlineData(0x0207, "", 3, "FF 04 00 00 00 1B 02 07")]
    [InlineData(0x0130, "02 04 00", 3, "FF 03 00 03 00 1B 01 30 02 04 00")]
    [InlineData(0x0130, "00 03 01", 4, "FF 04 00 03 00 1B 01 30 00 03 01")]
    public void Encode_MatchesCaptures(int command, string payload, int version, string wire)
    {
        var frame = new VivoFrame((ushort)command, Hex(payload));
        Assert.Equal(Hex(wire), frame.Encode(version));
    }

    [Fact]
    public void Decoder_HandlesEverySplitAndGarbage()
    {
        byte[] a = new VivoFrame(0x8207, [0, 40, 0, 255, 1]).Encode(4);
        byte[] b = new VivoFrame(0x8230, [0, 1, 4]).Encode(4);
        byte[] stream = [0x12, 0xFF, 0x09, .. a, 0x00, .. b];
        for (int split = 0; split <= stream.Length; split++)
        {
            var decoder = new VivoFrameDecoder();
            var frames = decoder.Feed(stream.AsSpan(0, split)).Concat(decoder.Feed(stream.AsSpan(split))).ToList();
            Assert.Equal(2, frames.Count);
            Assert.Equal(0x8207, frames[0].Command);
            Assert.Equal(new byte[] { 0, 40, 0, 255, 1 }, frames[0].Payload);
            Assert.Equal(0x8230, frames[1].Command);
        }
    }

    [Fact]
    public void Encode_RejectsOversizedPayload() =>
        Assert.Throws<ArgumentException>(() => new VivoFrame(1, new byte[255]).Encode(4));

    [Fact]
    public void Battery_ZeroIsALevelAndFfIsAbsent()
    {
        Assert.True(VivoParsers.TryParseBattery([0, 0, 255, 80, 5], out var l, out var r, out var c));
        Assert.Equal(new BatteryReading(0, true), l);
        Assert.Null(r);
        Assert.Equal(new BatteryReading(80, true), c);
        Assert.False(VivoParsers.TryParseBattery([1, 50, 50, 50, 0], out _, out _, out _));
    }

    [Fact]
    public void Wear_DecodesInCaseAndWornBits()
    {
        Assert.True(VivoParsers.TryParseWear([0, 6], out var wear));
        Assert.True(wear.LeftInEar);
        Assert.False(wear.LeftInCase);
        Assert.True(wear.RightInCase);
        Assert.False(wear.RightInEar);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 4 }, NoiseControlMode.NoiseCancellation)]
    [InlineData(new byte[] { 0, 1, 4 }, NoiseControlMode.Off)]
    [InlineData(new byte[] { 0, 2, 1 }, NoiseControlMode.Transparency)]
    public void Noise_ReportUsesSecondByte(byte[] payload, NoiseControlMode expected)
    {
        Assert.True(VivoParsers.TryParseNoise(payload, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void Noise_RejectsUnknownMode() => Assert.False(VivoParsers.TryParseNoise([0, 7, 0], out _));

    [Theory]
    [InlineData("00 31 2E 32", "1.2")]
    [InlineData("00 01 02 03 04 05 09 0C", "2.5.9")]
    [InlineData("00 32 2E 35 2E 39 5F 32 2E 35 2E 39", "2.5.9")]
    public void Firmware_ParsesTextAndBinary(string payload, string expected) =>
        Assert.Equal(expected, VivoParsers.ParseFirmware(Hex(payload)));
}
