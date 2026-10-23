// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace EncoDeck.Protocol.Brands.Sennheiser;

/// <summary>Sennheiser vendor GAIA v3. Does not use legacy CSR ack bits or a status prefix.</summary>
public sealed class SennheiserFrameCodec
{
    private readonly List<byte> _buffer = [];
    public static byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, 254);
        byte[] frame = new byte[8 + payload.Length];
        frame[0] = 0xFF; frame[1] = 3; frame[3] = (byte)payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 0x0495);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), command);
        payload.CopyTo(frame.AsSpan(8)); return frame;
    }
    public IReadOnlyList<BrandPacket> Feed(ReadOnlySpan<byte> bytes)
    {
        var packets = new List<BrandPacket>();
        foreach (byte value in bytes)
        {
            _buffer.Add(value);
            while (_buffer.Count >= 4)
            {
                var span = CollectionsMarshal.AsSpan(_buffer);
                byte version = span[1], flags = span[2];
                if (span[0] != 0xFF || version is not (3 or 4) || (flags & ~3) != 0 || (version == 3 && (flags & 2) != 0))
                { _buffer.RemoveAt(0); continue; }
                int header = (flags & 2) == 0 ? 4 : 5;
                if (span.Length < header) break;
                int length = header == 4 ? span[3] : BinaryPrimitives.ReadUInt16BigEndian(span[3..]);
                if (length > 1024) { _buffer.RemoveAt(0); continue; }
                int total = header + 4 + length + (flags & 1);
                if (span.Length < total) break;
                byte checksum = 0;
                if ((flags & 1) != 0) foreach (byte b in span[..total]) checksum ^= b;
                if (checksum == 0 && BinaryPrimitives.ReadUInt16BigEndian(span[header..]) == 0x0495)
                {
                    ushort command = BinaryPrimitives.ReadUInt16BigEndian(span[(header + 2)..]);
                    packets.Add(new(command, (byte)((command >> 7) & 3), span.Slice(header + 4, length).ToArray()));
                }
                _buffer.RemoveRange(0, total);
            }
        }
        return packets;
    }
}
