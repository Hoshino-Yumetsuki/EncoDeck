// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// One Xiaomi RCSP frame:
/// <c>FE DC BA | kind | opCode | length (2, big endian, = 1 + payload) | sequence | payload | EF</c>.
/// </summary>
/// <param name="Kind">Type byte (0xC4 for requests, 0x04 for the auth result report).</param>
/// <param name="OpCode">RCSP command: 0x02 battery/auth, 0xF2 set config, 0xF3 get config, 0x51 auth result.</param>
/// <param name="Sequence">Request sequence byte, echoed by the earbuds.</param>
/// <param name="Payload">Frame payload.</param>
public sealed record XiaomiFrame(byte Kind, byte OpCode, byte Sequence, byte[] Payload)
{
    public const byte Header0 = 0xFE;
    public const byte Header1 = 0xDC;
    public const byte Header2 = 0xBA;
    public const byte Footer = 0xEF;

    /// <summary>Header (3) + kind + opCode + length (2) + sequence + footer.</summary>
    public const int Overhead = 9;

    /// <summary>Largest payload accepted from the wire; anything bigger is treated as noise.</summary>
    public const int MaxPayload = 4096;

    public byte[] Encode()
    {
        var frame = new byte[Overhead + Payload.Length];
        frame[0] = Header0;
        frame[1] = Header1;
        frame[2] = Header2;
        frame[3] = Kind;
        frame[4] = OpCode;
        int length = 1 + Payload.Length;
        frame[5] = (byte)(length >> 8);
        frame[6] = (byte)length;
        frame[7] = Sequence;
        Payload.CopyTo(frame.AsSpan(8));
        frame[^1] = Footer;
        return frame;
    }

    public override string ToString() => $"kind=0x{Kind:X2} op=0x{OpCode:X2} seq=0x{Sequence:X2} payload={Convert.ToHexString(Payload)}";
}

/// <summary>Reassembles <see cref="XiaomiFrame"/>s from a byte stream that may split or merge frames.</summary>
public sealed class XiaomiFrameDecoder
{
    private readonly List<byte> _buffer = [];

    /// <summary>Appends <paramref name="data"/> and returns every complete frame found so far.</summary>
    public IReadOnlyList<XiaomiFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<XiaomiFrame>();
        while (true)
        {
            int start = FindHeader();
            if (start < 0)
            {
                // Keep a possible partial header at the end.
                int keep = Math.Min(_buffer.Count, 2);
                _buffer.RemoveRange(0, _buffer.Count - keep);
                if (keep > 0 && _buffer[0] != XiaomiFrame.Header0) _buffer.RemoveAt(0);
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 8) break;

            int length = (_buffer[5] << 8) | _buffer[6];
            if (length < 1 || length - 1 > XiaomiFrame.MaxPayload)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 8;
            if (_buffer.Count < total) break;
            if (_buffer[total - 1] != XiaomiFrame.Footer)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var payload = _buffer.GetRange(8, length - 1).ToArray();
            frames.Add(new XiaomiFrame(_buffer[3], _buffer[4], _buffer[7], payload));
            _buffer.RemoveRange(0, total);
        }
        return frames;
    }

    private int FindHeader()
    {
        for (int i = 0; i + 2 < _buffer.Count; i++)
        {
            if (_buffer[i] == XiaomiFrame.Header0 && _buffer[i + 1] == XiaomiFrame.Header1 && _buffer[i + 2] == XiaomiFrame.Header2) return i;
        }
        return -1;
    }
}
