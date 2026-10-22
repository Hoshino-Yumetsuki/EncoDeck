// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Xiaomi;

namespace EncoDeck.Tests.Protocol.Xiaomi;

public class XiaomiProtocolTests
{
    public static TheoryData<byte, byte, byte, byte[], string> Fixtures => new()
    {
        // Battery query (MiBudsClient capture), low-latency write, auth result report.
        { 0xC4, 0x02, 0x0B, [0xFF, 0xFF, 0xFF, 0xFF], "FEDCBAC40200050BFFFFFFFFEF" },
        { 0xC4, 0xF2, 0x90, [0x03, 0x00, 0x2F, 0x01], "FEDCBAC4F2000590 03002F01EF" },
        { 0x04, 0x51, 0x00, [0x03, 0x01], "FEDCBA045100030003 01EF" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Frame_EncodesAndDecodesAcrossSplits(byte kind, byte op, byte seq, byte[] payload, string hex)
    {
        var wire = Convert.FromHexString(hex.Replace(" ", ""));
        var frame = new XiaomiFrame(kind, op, seq, payload);
        Assert.Equal(wire, frame.Encode());

        for (int split = 0; split <= wire.Length; split++)
        {
            var decoder = new XiaomiFrameDecoder();
            var a = decoder.Feed([0x00, 0x01, 0x02, .. wire.AsSpan(0, split)]);
            var b = decoder.Feed([.. wire.AsSpan(split), .. wire]);
            var all = a.Concat(b).ToList();
            Assert.Equal(2, all.Count);
            Assert.All(all, f =>
            {
                Assert.Equal(kind, f.Kind);
                Assert.Equal(op, f.OpCode);
                Assert.Equal(seq, f.Sequence);
                Assert.Equal(payload, f.Payload);
            });
        }
    }

    [Fact]
    public void Decoder_SkipsFalseHeaderWithBadFooter()
    {
        var decoder = new XiaomiFrameDecoder();
        var good = new XiaomiFrame(0xC4, 0x02, 0x0B, [2, 2, 4, 7, 50, 60, 70]).Encode();
        var frames = decoder.Feed([0xFE, 0xDC, 0xBA, 0xC4, 0x02, 0x00, 0x02, 0x00, 0x00, 0x11, .. good]);
        var frame = Assert.Single(frames);
        Assert.True(XiaomiProtocol.TryParseBattery(frame, out var l, out var r, out var c));
        Assert.Equal((50, 60, 70), (l, r, c));
    }

    [Fact]
    public void Battery_OutOfRangeLevelsAreUnknown()
    {
        var frame = new XiaomiFrame(0xC4, 0x02, 0x0B, [2, 2, 4, 7, 80, 0xFF]);
        Assert.True(XiaomiProtocol.TryParseBattery(frame, out var l, out var r, out var c));
        Assert.Equal(80, l);
        Assert.Null(r);
        Assert.Null(c);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, new byte[] { 224, 205, 103, 156, 132, 213, 183, 32, 79, 23, 165, 217, 35, 67, 90, 104 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 215, 170, 85, 59, 84, 215, 123, 176, 201, 219, 112, 252, 201, 89, 164, 5 })]
    [InlineData(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 }, new byte[] { 149, 177, 97, 196, 26, 182, 62, 95, 90, 132, 6, 21, 75, 9, 249, 76 })]
    public void Auth_MatchesReferenceVectors(byte[] challenge, byte[] expected)
    {
        Assert.Equal(expected, XiaomiAuth.GetEncryptedAuthCheckData(challenge));
        Assert.True(XiaomiAuth.VerifyAuthResponse(challenge, expected));
        Assert.False(XiaomiAuth.VerifyAuthResponse(challenge, new byte[16]));
    }

    [Fact]
    public void Auth_PayloadsRoundTrip()
    {
        var random = XiaomiAuth.CreateRandomFactor();
        var payload = XiaomiAuth.BuildAuthCheckPayload(random);
        Assert.Equal(17, payload.Length);
        Assert.Equal(1, payload[0]);
        Assert.True(XiaomiAuth.TryParseAuthCheckResponse(payload, out var version, out var result));
        Assert.Equal(1, version);
        Assert.Equal(random, result);
        Assert.Throws<ArgumentException>(() => XiaomiAuth.GetEncryptedAuthCheckData([0]));
    }

    [Theory]
    [InlineData("Xiaomi Buds 5 Pro", "xiaomi-earbuds")]
    [InlineData("Mi True Wireless Earphones 2", "xiaomi-earbuds")]
    [InlineData("FlipBuds Pro", "xiaomi-earbuds")]
    [InlineData("my Redmi Buds", "redmi-earbuds")]
    [InlineData("Redmi Buds 6 Pro", "redmi-earbuds")]
    [InlineData("AirPods Pro", null)]
    [InlineData("Soundcore Liberty Air", null)]
    [InlineData("OPPO Enco X4", null)]
    [InlineData("Keyboard", null)]
    public void Profiles_MatchByName(string name, string? modelId)
    {
        var identity = new DeviceIdentity("id", name, new BluetoothAddress(0x112233445566));
        var best = XiaomiProfiles.CreateBuiltIn()
            .Select(p => (p, s: p.MatchScore(identity)))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .Select(x => x.p.ModelId)
            .FirstOrDefault();
        Assert.Equal(modelId, best);
    }

    [Fact]
    public void Profiles_AreGenericXiaomiOnXiaoAiSpp()
    {
        foreach (var profile in XiaomiProfiles.CreateBuiltIn().Cast<IControllableProfile>())
        {
            Assert.Equal("xiaomi", profile.Brand);
            Assert.True(profile.IsGeneric);
            Assert.Equal(profile.ModelId, profile.ImageKey);
            Assert.Equal([new Guid("00001101-0000-1000-8000-008584D01810")], profile.ControlServiceUuids);
        }
    }
}
