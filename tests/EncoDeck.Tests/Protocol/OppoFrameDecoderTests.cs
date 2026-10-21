// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Protocol;

public class OppoFrameDecoderTests
{
    private static readonly byte[] Battery = new OppoFrame(0x8106, 0xF0, [0x00, 0x02, 0x01, 0x64, 0x02, 0xD5]).Encode();
    private static readonly byte[] Anc = new OppoFrame(0x810C, 0x04, [0x00, 0x01, 0x01, 0x10, 0x00]).Encode();

    [Fact]
    public void SingleCompleteFrame()
    {
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push(Battery);
        Assert.Single(frames);
        Assert.Equal(0x8106, frames[0].Command);
        Assert.Equal(0, decoder.PendingBytes);
    }

    [Fact]
    public void HalfPacket_IsBufferedUntilComplete()
    {
        var decoder = new OppoFrameDecoder();
        Assert.Empty(decoder.Push(Battery.AsSpan(0, 4)));
        Assert.Empty(decoder.Push(Battery.AsSpan(4, 6)));
        var frames = decoder.Push(Battery.AsSpan(10));
        Assert.Single(frames);
        Assert.Equal(Battery, frames[0].Encode());
    }

    [Fact]
    public void ByteByByte_DeliversFrame()
    {
        var decoder = new OppoFrameDecoder();
        var frames = new List<OppoFrame>();
        foreach (byte b in Battery) frames.AddRange(decoder.Push([b]));
        Assert.Single(frames);
    }

    [Fact]
    public void StickyPackets_AreSplit()
    {
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push([.. Battery, .. Anc, .. Battery]);
        Assert.Equal(3, frames.Count);
        Assert.Equal(new ushort[] { 0x8106, 0x810C, 0x8106 }, frames.Select(f => f.Command));
    }

    [Fact]
    public void StickyPlusPartial_KeepsRemainder()
    {
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push([.. Battery, .. Anc.AsSpan(0, 5)]);
        Assert.Single(frames);
        Assert.Equal(5, decoder.PendingBytes);
        frames = decoder.Push(Anc.AsSpan(5));
        Assert.Single(frames);
        Assert.Equal(0x810C, frames[0].Command);
    }

    [Fact]
    public void LeadingGarbage_IsDiscarded()
    {
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push([0x00, 0x13, 0x37, .. Battery]);
        Assert.Single(frames);
        Assert.Equal(3, decoder.DiscardedBytes);
    }

    [Fact]
    public void FalseMagicWithBadLength_ResyncsToRealFrame()
    {
        var decoder = new OppoFrameDecoder();
        // 0xAA followed by a length below the minimum.
        var frames = decoder.Push([0xAA, 0x02, .. Battery]);
        Assert.Single(frames);
        Assert.Equal(0x8106, frames[0].Command);
    }

    [Fact]
    public void FalseMagicWithInconsistentPayloadLength_DoesNotSwallowRealFrame()
    {
        var decoder = new OppoFrameDecoder();
        // Looks like a header claiming 0x10 bytes but payload-length field disagrees.
        byte[] fake = [0xAA, 0x10, 0x00, 0x00, 0x01, 0x02, 0x03, 0x55, 0x55];
        var frames = decoder.Push([.. fake, .. Battery]);
        Assert.Single(frames);
        Assert.Equal(Battery, frames[0].Encode());
    }

    [Fact]
    public void MagicByteInsidePayload_DoesNotBreakFraming()
    {
        var withMagic = new OppoFrame(0x8122, 1, [0x00, 0xAA, 0x07, 0xAA, 0xAA]).Encode();
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push([.. withMagic, .. Anc]);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 0x00, 0xAA, 0x07, 0xAA, 0xAA }, frames[0].Payload);
    }

    [Fact]
    public void LargeInput_ManyFramesAcrossBufferBoundaries()
    {
        var decoder = new OppoFrameDecoder();
        var big = new OppoFrame(0x8122, 9, Enumerable.Range(0, OppoFrame.MaxSingleFramePayload).Select(i => (byte)i).ToArray()).Encode();
        var stream = Enumerable.Repeat(big, 20).SelectMany(b => b).ToArray();
        var frames = new List<OppoFrame>();
        for (int i = 0; i < stream.Length; i += 333)
        {
            frames.AddRange(decoder.Push(stream.AsSpan(i, Math.Min(333, stream.Length - i))));
        }
        Assert.Equal(20, frames.Count);
        Assert.All(frames, f => Assert.Equal(OppoFrame.MaxSingleFramePayload, f.Payload.Length));
        Assert.Equal(0, decoder.DiscardedBytes);
    }

    [Fact]
    public void PureNoise_IsDroppedWithoutGrowth()
    {
        var decoder = new OppoFrameDecoder();
        var noise = Enumerable.Range(0, 5000).Select(i => (byte)(i % 0xA9)).ToArray();
        Assert.Empty(decoder.Push(noise));
        Assert.True(decoder.PendingBytes < 1100);
    }
}
