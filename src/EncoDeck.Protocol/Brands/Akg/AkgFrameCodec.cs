// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.Akg;

/// <summary>Harman BES short commands: AA, command, byte length, payload (CmdBase).</summary>
public sealed class AkgFrameCodec
{
    private readonly List<byte> _buffer = [];
    public static byte[] Encode(byte command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 255) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] bytes = new byte[3 + payload.Length];
        bytes[0] = 0xaa; bytes[1] = command; bytes[2] = (byte)payload.Length;
        payload.CopyTo(bytes.AsSpan(3)); return bytes;
    }
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        List<byte[]> frames = [];
        foreach (byte b in data)
        {
            if (_buffer.Count == 0 && b != 0xaa) continue;
            _buffer.Add(b);
            if (_buffer.Count >= 3 && _buffer.Count == _buffer[2] + 3)
            { frames.Add(_buffer.ToArray()); _buffer.Clear(); }
        }
        return frames;
    }
}
