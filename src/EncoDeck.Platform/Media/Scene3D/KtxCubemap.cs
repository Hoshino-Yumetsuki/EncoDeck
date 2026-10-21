// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>Pixel formats of Filament's cmgen output that the renderer can upload directly.</summary>
public enum KtxPixelFormat
{
    /// <summary>GL_R11F_G11F_B10F, packed like DXGI_FORMAT_R11G11B10_FLOAT.</summary>
    R11G11B10Float,
    /// <summary>GL_RGBA16F.</summary>
    Rgba16Float,
    /// <summary>GL_RGBA8 / GL_SRGB8_ALPHA8.</summary>
    Rgba8,
}

/// <summary>
/// A KTX 1.1 cubemap as written by Filament's cmgen (what <c>KTXLoader.createIndirectLight</c> reads):
/// faces in +X, -X, +Y, -Y, +Z, -Z order for each mip level, and for an IBL the 9 pre-scaled
/// spherical-harmonics coefficients of the irradiance in the <c>sh</c> key/value entry.
/// </summary>
public sealed class KtxCubemap
{
    private KtxCubemap(KtxPixelFormat format, int size, byte[][][] levels, Vector3[]? sh)
    {
        Format = format;
        Size = size;
        Levels = levels;
        SphericalHarmonics = sh;
    }

    public KtxPixelFormat Format { get; }
    /// <summary>Edge length of level 0.</summary>
    public int Size { get; }
    /// <summary>[level][face] tightly packed rows.</summary>
    public byte[][][] Levels { get; }
    /// <summary>3 bands (9 coefficients), or null when the file has none.</summary>
    public Vector3[]? SphericalHarmonics { get; }

    public int BytesPerPixel => Format switch { KtxPixelFormat.Rgba16Float => 8, _ => 4 };

    private static uint U32(ReadOnlySpan<byte> data, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    public static KtxCubemap Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || !data[..12].SequenceEqual(Identifier)) throw new InvalidDataException("Not a KTX 1.1 file.");
        bool little = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) == 0x04030201;
        uint glInternalFormat = U32(data, 28, little);
        int width = (int)U32(data, 36, little), height = (int)U32(data, 40, little), faces = (int)U32(data, 52, little), mips = Math.Max(1, (int)U32(data, 56, little)), kvBytes = (int)U32(data, 60, little);
        if (faces != 6 || width != height || width <= 0 || width > 4096 || mips > 13) throw new InvalidDataException("Not a supported KTX cubemap.");
        var format = glInternalFormat switch
        {
            0x8C3A => KtxPixelFormat.R11G11B10Float,
            0x881A => KtxPixelFormat.Rgba16Float,
            0x8058 or 0x8C43 => KtxPixelFormat.Rgba8,
            _ => throw new InvalidDataException($"Unsupported KTX format 0x{glInternalFormat:X}."),
        };
        if (64L + kvBytes > data.Length) throw new InvalidDataException("Corrupt KTX metadata.");
        Vector3[]? sh = null;
        for (int pos = 64; pos + 4 <= 64 + kvBytes;)
        {
            int size = (int)U32(data, pos, little);
            if (size < 0 || pos + 4 + size > 64 + kvBytes) break;
            var pair = data.Slice(pos + 4, size);
            int zero = pair.IndexOf((byte)0);
            if (zero > 0 && Encoding.ASCII.GetString(pair[..zero]) == "sh") sh = ParseSphericalHarmonics(Encoding.ASCII.GetString(pair[(zero + 1)..]).TrimEnd('\0'));
            pos += 4 + ((size + 3) & ~3);
        }

        int bpp = format == KtxPixelFormat.Rgba16Float ? 8 : 4;
        var levels = new byte[mips][][];
        int offset = 64 + kvBytes;
        for (int level = 0; level < mips; level++)
        {
            int edge = Math.Max(1, width >> level);
            int faceBytes = edge * edge * bpp;
            if (offset + 4 > data.Length) throw new InvalidDataException("Truncated KTX file.");
            int imageSize = (int)U32(data, offset, little);
            offset += 4;
            if (imageSize < faceBytes) throw new InvalidDataException("Unexpected KTX image size.");
            levels[level] = new byte[6][];
            for (int face = 0; face < 6; face++)
            {
                if (offset + imageSize > data.Length) throw new InvalidDataException("Truncated KTX file.");
                // Rows of 4-byte pixels are always 4-byte aligned, so each face is tightly packed.
                levels[level][face] = data.Slice(offset, faceBytes).ToArray();
                offset += (imageSize + 3) & ~3;
            }
        }
        return new KtxCubemap(format, width, levels, sh);
    }

    private static Vector3[]? ParseSphericalHarmonics(string text)
    {
        var values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : float.NaN)
            .ToArray();
        if (values.Length < 27 || values.Take(27).Any(v => !float.IsFinite(v))) return null;
        return Enumerable.Range(0, 9).Select(i => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToArray();
    }
}
