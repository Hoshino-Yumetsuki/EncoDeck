// SPDX-License-Identifier: GPL-3.0-only
//
// Wire format (see docs/PROTOCOL_FINDINGS.md for sources and evidence):
//
// Link frame
//   0xAA | LEN (1–2 byte little-endian base-128 varint) | CTRL | 0x00 | [FSN] | chunk
//   LEN counts every byte after itself. CTRL bits 0–1: 0 single, 1 first, 2 middle, 3 last fragment.
//   FSN (fragment sequence number) is present only when CTRL != 0. No checksum.
//
// Application packet (carried in one or more link-frame chunks)
//   CMD (u16 LE; bit 15 = response) | SEQ (u8) | PLEN (u16 LE) | payload
//
// A small single-frame packet therefore looks like:
//   AA 07 00 00 06 01 F0 00 00            (GET battery, seq F0, empty payload)
using System.Buffers.Binary;

namespace EncoDeck.Protocol.Oppo;

/// <summary>One application-layer packet.</summary>
public readonly record struct OppoFrame(ushort Command, byte Sequence, byte[] Payload)
{
    public const byte Magic = 0xAA;
    public const ushort ResponseFlag = 0x8000;
    public const int PacketHeaderLength = 5;

    /// <summary>Largest link frame the earbuds accept over RFCOMM (from the official app).</summary>
    public const int MaxLinkFrameLength = 660;

    /// <summary>Header bytes of a single-frame link frame with a 1-byte LEN.</summary>
    public const int HeaderLength = 4 + PacketHeaderLength;

    /// <summary>Largest payload that still fits in one unfragmented link frame.</summary>
    public const int MaxSingleFramePayload = MaxLinkFrameLength - 1 - 2 - 2 - PacketHeaderLength;

    /// <summary>Upper bound for a reassembled packet's payload.</summary>
    public const int MaxPayloadLength = 16 * 1024;

    public bool IsResponse => (Command & ResponseFlag) != 0;

    /// <summary>The request command id this frame answers (or the command itself for requests/notifications).</summary>
    public ushort BaseCommand => (ushort)(Command & ~ResponseFlag);

    /// <summary>Command group: high byte without the response flag (0x01 GET, 0x02 notify, 0x04 SET, 0x05 device request).</summary>
    public byte Group => (byte)(BaseCommand >> 8);

    public byte[] EncodePacket()
    {
        if (Payload.Length > MaxPayloadLength) throw new ArgumentException($"Payload of {Payload.Length} bytes exceeds {MaxPayloadLength}.");
        var bytes = new byte[PacketHeaderLength + Payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, Command);
        bytes[2] = Sequence;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(3), (ushort)Payload.Length);
        Payload.CopyTo(bytes, PacketHeaderLength);
        return bytes;
    }

    public static bool TryParsePacket(ReadOnlySpan<byte> packet, out OppoFrame frame)
    {
        frame = default;
        if (packet.Length < PacketHeaderLength) return false;
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]);
        if (payloadLength != packet.Length - PacketHeaderLength) return false;
        frame = new OppoFrame(BinaryPrimitives.ReadUInt16LittleEndian(packet), packet[2], packet[PacketHeaderLength..].ToArray());
        return true;
    }

    /// <summary>Wire bytes as one or more link frames (fragmented when the packet exceeds one frame).</summary>
    public IReadOnlyList<byte[]> EncodeLinkFrames() => OppoLink.Encode(EncodePacket());

    /// <summary>All wire bytes concatenated. Convenient for tests and single-frame packets.</summary>
    public byte[] Encode()
    {
        var frames = EncodeLinkFrames();
        return frames.Count == 1 ? frames[0] : frames.SelectMany(f => f).ToArray();
    }

    /// <summary>Parses exactly one complete single (unfragmented) link frame.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out OppoFrame frame)
    {
        frame = default;
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push(data);
        if (frames.Count != 1 || decoder.PendingBytes != 0 || decoder.DiscardedBytes != 0) return false;
        frame = frames[0];
        return true;
    }

    public override string ToString() =>
        $"cmd=0x{Command:X4} seq=0x{Sequence:X2} len={Payload.Length} payload={Convert.ToHexString(Payload)}";
}

/// <summary>Link-layer encoding (varint length, fragmentation).</summary>
public static class OppoLink
{
    public const byte CtrlSingle = 0;
    public const byte CtrlFirst = 1;
    public const byte CtrlMiddle = 2;
    public const byte CtrlLast = 3;

    public static int VarintLength(int value) => value < 0x80 ? 1 : 2;

    public static IReadOnlyList<byte[]> Encode(ReadOnlySpan<byte> packet)
    {
        // Single frame: AA LEN(1-2) CTRL 00 packet; LEN = 2 + packet.
        int singleLen = 2 + packet.Length;
        if (1 + VarintLength(singleLen) + singleLen <= OppoFrame.MaxLinkFrameLength)
        {
            return [Build(CtrlSingle, null, packet)];
        }

        // Fragmented: AA LEN(2) CTRL 00 FSN chunk; LEN = 3 + chunk.
        int maxChunk = OppoFrame.MaxLinkFrameLength - 1 - 2 - 3;
        var frames = new List<byte[]>();
        int offset = 0;
        byte fsn = 0;
        while (offset < packet.Length)
        {
            int size = Math.Min(maxChunk, packet.Length - offset);
            byte ctrl = offset == 0 ? CtrlFirst : offset + size >= packet.Length ? CtrlLast : CtrlMiddle;
            frames.Add(Build(ctrl, fsn++, packet.Slice(offset, size)));
            offset += size;
        }
        return frames;
    }

    private static byte[] Build(byte ctrl, byte? fsn, ReadOnlySpan<byte> chunk)
    {
        int len = 2 + (fsn.HasValue ? 1 : 0) + chunk.Length;
        int varint = VarintLength(len);
        var bytes = new byte[1 + varint + len];
        bytes[0] = OppoFrame.Magic;
        if (varint == 1)
        {
            bytes[1] = (byte)len;
        }
        else
        {
            bytes[1] = (byte)((len & 0x7F) | 0x80);
            bytes[2] = (byte)(len >> 7);
        }
        int p = 1 + varint;
        bytes[p++] = ctrl;
        bytes[p++] = 0x00;
        if (fsn.HasValue) bytes[p++] = fsn.Value;
        chunk.CopyTo(bytes.AsSpan(p));
        return bytes;
    }
}
