// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Protocol;

public class OppoFrameTests
{
    [Fact]
    public void Encode_ProducesDocumentedLayout()
    {
        // Matches the frame the Probe sent to a real Enco X4.
        var frame = new OppoFrame(OppoCommand.QueryBattery, 0xF0, []);
        Assert.Equal(Convert.FromHexString("AA0700000601F00000"), frame.Encode());
    }

    [Fact]
    public void Encode_SetNoiseControl_MatchesHardwareVerifiedFrame()
    {
        // Enco X4 acknowledged this frame (protocol index 2 = transparency parent).
        var frame = new OppoFrame(OppoCommand.SetNoiseControl, 0xF0, [0x01, 0x01, 0x04]);
        Assert.Equal(Convert.FromHexString("AA0A00000404F003000101" + "04"), frame.Encode());
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new OppoFrame(0x8122, 0x42, [1, 2, 3, 4, 5]);
        Assert.True(OppoFrame.TryParse(original.Encode(), out var parsed));
        Assert.Equal(original.Command, parsed.Command);
        Assert.Equal(original.Sequence, parsed.Sequence);
        Assert.Equal(original.Payload, parsed.Payload);
        Assert.True(parsed.IsResponse);
        Assert.Equal((ushort)0x0122, parsed.BaseCommand);
        Assert.Equal((byte)0x01, parsed.Group);
    }

    [Fact]
    public void Length127_UsesOneVarintByte()
    {
        // LEN = 2 + 5 + 120 = 127
        var bytes = new OppoFrame(0x8122, 1, new byte[120]).Encode();
        Assert.Equal(0x7F, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(1 + 1 + 127, bytes.Length);
    }

    [Fact]
    public void Length128_UsesTwoVarintBytes()
    {
        // LEN = 2 + 5 + 121 = 128 -> varint 80 01
        var bytes = new OppoFrame(0x8122, 1, new byte[121]).Encode();
        Assert.Equal(0x80, bytes[1]);
        Assert.Equal(0x01, bytes[2]);
        Assert.Equal(0x00, bytes[3]); // CTRL single
        Assert.Equal(1 + 2 + 128, bytes.Length);
        Assert.True(OppoFrame.TryParse(bytes, out var parsed));
        Assert.Equal(121, parsed.Payload.Length);
    }

    [Fact]
    public void LargestSingleFrame_IsNotFragmented()
    {
        var frames = new OppoFrame(0x0418, 1, new byte[OppoFrame.MaxSingleFramePayload]).EncodeLinkFrames();
        Assert.Single(frames);
        Assert.Equal(OppoFrame.MaxLinkFrameLength, frames[0].Length);
    }

    [Fact]
    public void OversizedPacket_IsFragmentedAndReassembled()
    {
        var payload = Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray();
        var frame = new OppoFrame(0x8122, 7, payload);
        var linkFrames = frame.EncodeLinkFrames();
        Assert.Equal(3, linkFrames.Count);
        Assert.All(linkFrames, f => Assert.True(f.Length <= OppoFrame.MaxLinkFrameLength));
        Assert.Equal(new byte[] { OppoLink.CtrlFirst, OppoLink.CtrlMiddle, OppoLink.CtrlLast }, linkFrames.Select(f => f[3]));
        Assert.Equal(new byte[] { 0, 1, 2 }, linkFrames.Select(f => f[5]));

        var decoder = new OppoFrameDecoder();
        var decoded = linkFrames.SelectMany(f => decoder.Push(f)).ToList();
        Assert.Single(decoded);
        Assert.Equal(payload, decoded[0].Payload);
        Assert.Equal(0x8122, decoded[0].Command);
    }

    [Fact]
    public void Encode_RejectsAbsurdPayload()
    {
        var frame = new OppoFrame(0x0418, 1, new byte[OppoFrame.MaxPayloadLength + 1]);
        Assert.Throws<ArgumentException>(() => frame.Encode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA07000006")]                 // truncated header
    [InlineData("BB0700000601F00000")]         // wrong magic
    [InlineData("AA0600000601F00000")]         // length below minimum
    [InlineData("AA0700000601F00100")]         // payload length disagrees with LEN
    [InlineData("AA0800000601F00000")]         // LEN larger than data
    [InlineData("AA0700000601F0000000")]       // trailing byte
    [InlineData("AA0704000601F00000")]         // CTRL has undefined bits
    [InlineData("AA870000000601F00000")]       // non-canonical 2-byte varint for 7
    public void TryParse_RejectsMalformed(string hex)
    {
        Assert.False(OppoFrame.TryParse(Convert.FromHexString(hex), out _));
    }

    [Fact]
    public void SequenceAllocator_SkipsFixedValuesAndWraps()
    {
        var allocator = new OppoSequenceAllocator();
        var seen = Enumerable.Range(0, 0xEF * 2).Select(_ => allocator.Next()).ToList();
        Assert.DoesNotContain(OppoSequenceAllocator.FixedQuery, seen);
        Assert.DoesNotContain(OppoSequenceAllocator.FixedSet, seen);
        Assert.Equal(1, seen[0]);
        Assert.Equal(0xEF, seen[0xEE]);
        Assert.Equal(1, seen[0xEF]);
    }
}
