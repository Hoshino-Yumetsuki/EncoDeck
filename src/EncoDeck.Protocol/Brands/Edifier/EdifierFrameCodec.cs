// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>One decoded EDIFIER frame: command byte and data bytes.</summary>
public sealed record EdifierFrame(byte Command, byte[] Payload);

/// <summary>
/// EDIFIER framing. Requests are <c>AA len cmd data… chkHi chkLo</c>, responses start with
/// <c>BB</c> or <c>CC</c> instead of <c>AA</c>; <c>len</c> = 1 + data length and the checksum is
/// 8217 plus the sum of every preceding byte (16-bit, big-endian).
/// </summary>
public static class EdifierFrameCodec
{
    /// <summary>Largest data length that fits the one-byte length field.</summary>
    public const int MaxPayload = 254;

    /// <summary>
    /// Encodes a request. Upstream allocated one byte too few (the checksum overwrote the last data
    /// byte); this follows the documented layout instead, as the HeyBuds re-port does.
    /// </summary>
    public static byte[] Encode(byte command, ReadOnlySpan<byte> payload) => Encode(EdifierConstants.RequestHead, command, payload);

    /// <summary>Encodes a frame with an explicit head byte (tests use it to build responses).</summary>
    public static byte[] Encode(byte head, byte command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayload);
        int length = 1 + payload.Length;
        var frame = new byte[length + 4];
        frame[0] = head;
        frame[1] = (byte)length;
        frame[2] = command;
        payload.CopyTo(frame.AsSpan(3));
        ushort sum = Checksum(frame.AsSpan(0, length + 2));
        frame[length + 2] = (byte)(sum >> 8);
        frame[length + 3] = (byte)sum;
        return frame;
    }

    internal static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        ushort sum = EdifierConstants.ChecksumSeed;
        foreach (byte b in bytes) sum = unchecked((ushort)(sum + b));
        return sum;
    }
}

/// <summary>Incremental decoder for device responses; keeps partial frames across reads.</summary>
public sealed class EdifierFrameDecoder
{
    private const int MaxBuffered = 8192;
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<EdifierFrame> Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) _buffer.Add(b);
        var frames = new List<EdifierFrame>();
        while (_buffer.Count > 0)
        {
            int start = _buffer.FindIndex(b => b is EdifierConstants.ResponseHeadBb or EdifierConstants.ResponseHeadCc);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 2) break;
            int length = _buffer[1];
            if (length < 1)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 4;
            if (_buffer.Count < total) break;

            var bytes = _buffer.GetRange(0, total).ToArray();
            ushort sum = EdifierFrameCodec.Checksum(bytes.AsSpan(0, total - 2));
            if (bytes[total - 2] != (byte)(sum >> 8) || bytes[total - 1] != (byte)sum)
            {
                // Bad checksum: skip one byte and resynchronize on the next head.
                _buffer.RemoveAt(0);
                continue;
            }
            frames.Add(new EdifierFrame(bytes[2], bytes[3..(total - 2)]));
            _buffer.RemoveRange(0, total);
        }
        if (_buffer.Count > MaxBuffered) _buffer.Clear();
        return frames;
    }
}
