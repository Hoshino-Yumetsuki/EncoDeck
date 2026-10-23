// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;
namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>Sound Connect v2 table 1 parameter layouts (im.f/h/i/l/m/g).</summary>
public static class SonyV2Noise
{
    public static byte? InquiredType(byte function) => function switch
    { 100 => 19, 101 => 20, 104 => 21, 106 => 22, 107 => 23, 108 => 24, 109 => 25, _ => null };
    public static bool TryRead(ReadOnlySpan<byte> p, out NoiseControlMode mode)
    {
        mode = NoiseControlMode.Off;
        if (p.Length < 7 || p[2] > 1 || p[3] > 1) return false;
        int length = p[1] switch { 19 or 20 or 23 or 24 => 7, 21 or 22 => 8, 25 => 9, _ => 0 };
        if (p.Length != length) return false;
        if (p[3] == 0) return true;
        if (p[1] is 19 or 20) mode = p[6] > 0 ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
        else mode = p[4] == 1 ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
        return true;
    }
    public static byte[]? SetPayload(ReadOnlySpan<byte> current, NoiseControlMode target)
    {
        if (!TryRead(current, out _)) return null;
        byte[] p = current.ToArray(); p[0] = 0x68; p[2] = 1;
        p[3] = target == NoiseControlMode.Off ? (byte)0 : (byte)1;
        if (p[1] is 19 or 20)
        {
            p[4] = target == NoiseControlMode.NoiseCancellation ? (p[1] == 20 ? (byte)2 : (byte)1) : (byte)0;
            p[6] = target == NoiseControlMode.Transparency ? (byte)20 : (byte)0;
        }
        else
        {
            p[4] = target == NoiseControlMode.Transparency ? (byte)1 : (byte)0;
            // Preserve auto/dual choice and noise-adaptation fields supplied by the device.
            int level = p[1] is 21 or 22 ? 7 : 6;
            if (target == NoiseControlMode.Transparency && p[level] == 0) p[level] = 20;
        }
        return p[1..];
    }
}
