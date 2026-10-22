// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.Media;

/// <summary>Huawei overlap video: left half is RGB, right half is its grayscale alpha mask.</summary>
public static class SplitAlphaFrame
{
    public static void Compose(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height)
    {
        if (width <= 0 || (width & 1) != 0 || height <= 0 || (long)width * height * 4 > source.Length || (long)width * height * 2 > destination.Length)
            throw new ArgumentException("Invalid split-alpha frame dimensions.");
        int half = width / 2;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < half; x++)
            {
                int rgb = (y * width + x) * 4, mask = rgb + half * 4, output = (y * half + x) * 4;
                byte alpha = source[mask + 2];
                destination[output] = (byte)((source[rgb] * alpha + 127) / 255);
                destination[output + 1] = (byte)((source[rgb + 1] * alpha + 127) / 255);
                destination[output + 2] = (byte)((source[rgb + 2] * alpha + 127) / 255);
                destination[output + 3] = alpha;
            }
    }
}
