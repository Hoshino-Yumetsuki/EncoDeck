// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>Wire format spoken by a HUAWEI audio device. Both share the same command numbers.</summary>
public enum HuaweiProtocol
{
    /// <summary>Classic FreeBuds framing: <c>5A 00 len_lo len_hi svc cmd TLV… crc_hi crc_lo</c>.</summary>
    Legacy,

    /// <summary>HarmonyOS "MBB" framing: <c>5A len_hi len_lo ctrl svc cmd TLV… crc_hi crc_lo</c>.</summary>
    Mbb,
}

/// <summary>One decoded HUAWEI frame. <see cref="Command"/> is <c>service &lt;&lt; 8 | command</c>; the payload is a TLV list.</summary>
public readonly record struct HuaweiFrame(ushort Command, byte[] Payload)
{
    public byte Service => (byte)(Command >> 8);

    public byte CommandId => (byte)Command;
}

/// <summary>Encodes requests and incrementally decodes a received byte stream (partial frames are buffered).</summary>
public interface IHuaweiFrameCodec
{
    HuaweiProtocol Protocol { get; }

    byte[] Encode(ushort command, ReadOnlySpan<byte> payload);

    /// <summary>Appends received bytes and returns every complete, CRC-valid frame.</summary>
    IReadOnlyList<HuaweiFrame> Feed(ReadOnlySpan<byte> data);
}

/// <summary>CRC and TLV helpers shared by both framings.</summary>
public static class HuaweiWire
{
    /// <summary>Largest TLV body accepted (matches the HeyBuds re-port's bound).</summary>
    public const int MaxPayload = 4096;

    /// <summary>CRC-16/XMODEM (poly 0x1021, init 0, no reflection), appended big-endian.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>Parses a <c>[type][len][value…]</c> list; a truncated trailing entry is ignored. Later duplicates win.</summary>
    public static Dictionary<byte, byte[]> ParseTlv(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<byte, byte[]>();
        int offset = 0;
        while (offset + 2 <= payload.Length)
        {
            byte type = payload[offset];
            int length = payload[offset + 1];
            int end = offset + 2 + length;
            if (end > payload.Length) break;
            fields[type] = payload[(offset + 2)..end].ToArray();
            offset = end;
        }
        return fields;
    }

    public static IHuaweiFrameCodec CreateCodec(HuaweiProtocol protocol) =>
        protocol == HuaweiProtocol.Mbb ? new MbbFrameCodec() : new LegacyHuaweiFrameCodec();

    internal static byte[] Finish(byte[] frame)
    {
        ushort crc = Crc16(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)crc;
        return frame;
    }

    internal static bool CrcMatches(List<byte> buffer, int total)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer)[..total];
        ushort crc = Crc16(span[..^2]);
        return span[^2] == (byte)(crc >> 8) && span[^1] == (byte)crc;
    }
}

/// <summary>
/// Classic FreeBuds framing. The length (little-endian, as the reference reads it) counts the command
/// byte, the TLVs and the 2-byte CRC; the CRC covers everything from 0x5A to the last TLV byte.
/// There is no byte escaping in either HUAWEI framing.
/// </summary>
public sealed class LegacyHuaweiFrameCodec : IHuaweiFrameCodec
{
    private const int HeaderSize = 6; // 5A 00 len len svc cmd
    private readonly List<byte> _buffer = [];

    public HuaweiProtocol Protocol => HuaweiProtocol.Legacy;

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, HuaweiWire.MaxPayload);
        int length = payload.Length + 3;
        var frame = new byte[HeaderSize + payload.Length + 2];
        frame[0] = 0x5A;
        frame[1] = 0x00;
        frame[2] = (byte)length;
        frame[3] = (byte)(length >> 8);
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)command;
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return HuaweiWire.Finish(frame);
    }

    public IReadOnlyList<HuaweiFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<HuaweiFrame>();
        while (_buffer.Count > 0)
        {
            int start = _buffer.IndexOf(0x5A);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 2) break;
            if (_buffer[1] != 0x00)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            if (_buffer.Count < HeaderSize) break;
            int length = _buffer[2] | (_buffer[3] << 8);
            if (length < 3 || length > HuaweiWire.MaxPayload + 3)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 5;
            if (_buffer.Count < total) break;
            if (!HuaweiWire.CrcMatches(_buffer, total))
            {
                _buffer.RemoveAt(0);
                continue;
            }
            var command = (ushort)((_buffer[4] << 8) | _buffer[5]);
            frames.Add(new HuaweiFrame(command, _buffer.GetRange(HeaderSize, length - 3).ToArray()));
            _buffer.RemoveRange(0, total);
        }
        if (_buffer.Count > 2 * (HuaweiWire.MaxPayload + 16)) _buffer.Clear();
        return frames;
    }
}

/// <summary>
/// HarmonyOS MBB framing: big-endian length (= TLV length + 3, excluding the CRC), a control byte
/// (0 = single frame, 1/2/3 = first/middle/last fragment) and the same CRC as the legacy framing.
/// Fragmented frames are skipped: their reassembly layout is unverified upstream, so they are never
/// interpreted as control state.
/// </summary>
public sealed class MbbFrameCodec : IHuaweiFrameCodec
{
    private const int HeaderSize = 6; // 5A len len ctrl svc cmd
    private readonly List<byte> _buffer = [];

    public HuaweiProtocol Protocol => HuaweiProtocol.Mbb;

    /// <summary>Number of fragment frames received and dropped (diagnostics).</summary>
    public int DroppedFragments { get; private set; }

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, HuaweiWire.MaxPayload);
        int length = payload.Length + 3;
        var frame = new byte[length + 5];
        frame[0] = 0x5A;
        frame[1] = (byte)(length >> 8);
        frame[2] = (byte)length;
        frame[3] = 0x00;
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)command;
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return HuaweiWire.Finish(frame);
    }

    public IReadOnlyList<HuaweiFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<HuaweiFrame>();
        while (_buffer.Count > 0)
        {
            int start = _buffer.IndexOf(0x5A);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4) break;
            int length = (_buffer[1] << 8) | _buffer[2];
            byte control = _buffer[3];
            if (length < 3 || length > HuaweiWire.MaxPayload + 3 || control > 3)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 5;
            if (_buffer.Count < total) break;
            if (!HuaweiWire.CrcMatches(_buffer, total))
            {
                _buffer.RemoveAt(0);
                continue;
            }
            if (control == 0)
            {
                var command = (ushort)((_buffer[4] << 8) | _buffer[5]);
                frames.Add(new HuaweiFrame(command, _buffer.GetRange(HeaderSize, length - 3).ToArray()));
            }
            else
            {
                DroppedFragments++;
            }
            _buffer.RemoveRange(0, total);
        }
        if (_buffer.Count > 2 * (HuaweiWire.MaxPayload + 16)) _buffer.Clear();
        return frames;
    }
}
