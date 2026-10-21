// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace EncoDeck.Platform.Media;

/// <summary>One animation frame: where it sits on the canvas and its own image data.</summary>
public sealed record AnimatedWebpFrame(int X, int Y, int Width, int Height, int DurationMs, bool DisposeToBackground, bool Blend, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads the container of an animated WebP (RIFF / VP8X / ANIM / ANMF) and rebuilds any frame as a
/// standalone still WebP that Windows' WebP decoder can open. Turntables such as HeyMelody's
/// detail-page model store every viewing angle as a complete, non-blended frame, so each angle can
/// be decoded on its own, on demand. Only container bytes are handled here; nothing is decoded.
/// </summary>
public sealed class AnimatedWebp
{
    private AnimatedWebp(int width, int height, IReadOnlyList<AnimatedWebpFrame> frames)
    {
        CanvasWidth = width;
        CanvasHeight = height;
        Frames = frames;
    }

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    public IReadOnlyList<AnimatedWebpFrame> Frames { get; }

    /// <summary>
    /// True when every frame replaces its area without blending: frame <c>i</c> then looks right on
    /// its own (over a transparent canvas), which is what random access by angle needs.
    /// </summary>
    public bool FramesAreIndependent => Frames.All(f => !f.Blend);

    public static AnimatedWebp? TryParse(ReadOnlyMemory<byte> file)
    {
        var data = file.Span;
        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WEBP"u8)) return null;
        int canvasWidth = 0, canvasHeight = 0;
        var frames = new List<AnimatedWebpFrame>();
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            var tag = data.Slice(pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (size > (uint)(data.Length - pos - 8)) return null;
            var body = data.Slice(pos + 8, (int)size);
            if (tag.SequenceEqual("VP8X"u8) && body.Length >= 10)
            {
                canvasWidth = 1 + U24(body[4..]);
                canvasHeight = 1 + U24(body[7..]);
            }
            else if (tag.SequenceEqual("ANMF"u8) && body.Length > 16)
            {
                byte flags = body[15];
                frames.Add(new AnimatedWebpFrame(
                    X: 2 * U24(body),
                    Y: 2 * U24(body[3..]),
                    Width: 1 + U24(body[6..]),
                    Height: 1 + U24(body[9..]),
                    DurationMs: U24(body[12..]),
                    DisposeToBackground: (flags & 0x01) != 0,
                    Blend: (flags & 0x02) == 0,
                    Payload: file.Slice(pos + 8 + 16, (int)size - 16)));
            }
            pos += 8 + (int)size + (int)(size & 1);
        }
        if (frames.Count == 0 || canvasWidth <= 0 || canvasHeight <= 0) return null;
        return new AnimatedWebp(canvasWidth, canvasHeight, frames);
    }

    /// <summary>Frame <paramref name="index"/> as a standalone (still) WebP file.</summary>
    public byte[] ToStillWebp(int index)
    {
        var frame = Frames[index];
        var payload = frame.Payload.Span;
        bool alpha = ContainsChunk(payload, "ALPH"u8);
        int length = 4 + 18 + payload.Length;
        var file = new byte[8 + length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)length);
        "WEBP"u8.CopyTo(file.AsSpan(8));
        "VP8X"u8.CopyTo(file.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 10);
        file[20] = alpha ? (byte)0x10 : (byte)0;
        WriteU24(file.AsSpan(24), frame.Width - 1);
        WriteU24(file.AsSpan(27), frame.Height - 1);
        payload.CopyTo(file.AsSpan(30));
        return file;
    }

    private static bool ContainsChunk(ReadOnlySpan<byte> chunks, ReadOnlySpan<byte> tag)
    {
        int pos = 0;
        while (pos + 8 <= chunks.Length)
        {
            if (chunks.Slice(pos, 4).SequenceEqual(tag)) return true;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunks[(pos + 4)..]);
            pos += 8 + (int)size + (int)(size & 1);
        }
        return false;
    }

    private static int U24(ReadOnlySpan<byte> b) => b[0] | (b[1] << 8) | (b[2] << 16);

    private static void WriteU24(Span<byte> b, int value)
    {
        b[0] = (byte)value;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)(value >> 16);
    }
}
