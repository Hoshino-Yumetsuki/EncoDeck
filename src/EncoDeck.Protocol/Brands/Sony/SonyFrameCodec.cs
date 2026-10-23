// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>MDR RFCOMM framing, independently implemented from Sound Connect Dl.a / Pm.b.</summary>
public sealed class SonyFrameCodec
{
    private readonly List<byte> _buffer = [];
    private bool _started, _escaped;
    public static byte[] Encode(byte type, byte sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4096) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] raw = new byte[payload.Length + 7];
        raw[0] = type; raw[1] = sequence;
        BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(2), payload.Length);
        payload.CopyTo(raw.AsSpan(6));
        foreach (byte b in raw.AsSpan(0, raw.Length - 1)) raw[^1] = unchecked((byte)(raw[^1] + b));
        List<byte> result = [0x3e];
        foreach (byte b in raw)
            if (b is 0x3c or 0x3d or 0x3e) { result.Add(0x3d); result.Add((byte)(b & ~0x10)); }
            else result.Add(b);
        result.Add(0x3c);
        return result.ToArray();
    }

    public IReadOnlyList<SonyFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        List<SonyFrame> frames = [];
        foreach (byte b in bytes)
        {
            if (b == 0x3e) { _buffer.Clear(); _started = true; _escaped = false; continue; }
            if (!_started) continue;
            if (b == 0x3c)
            {
                if (!_escaped && _buffer.Count >= 7)
                {
                    byte[] raw = _buffer.ToArray();
                    int length = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(2));
                    byte sum = 0;
                    foreach (byte value in raw.AsSpan(0, raw.Length - 1)) sum = unchecked((byte)(sum + value));
                    if (length == raw.Length - 7 && raw[^1] == sum && raw[1] <= 1)
                        frames.Add(new(raw[0], raw[1], raw[6..^1]));
                }
                _started = false; _buffer.Clear(); continue;
            }
            if (_escaped) { _buffer.Add((byte)(b | 0x10)); _escaped = false; }
            else if (b == 0x3d) _escaped = true;
            else _buffer.Add(b);
            if (_buffer.Count > 4103) { _started = false; _buffer.Clear(); }
        }
        return frames;
    }
}

public sealed record SonyFrame(byte Type, byte Sequence, byte[] Payload);
