// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>Pure decoders for vivo report payloads. Each returns false for malformed or failed reports.</summary>
public static class VivoParsers
{
    /// <summary>
    /// <c>[00][left][right][case][charging]</c>; a level above 100 (0xFF) means the part is absent.
    /// Charging bits: 1 = left, 2 = right, 4 = case.
    /// </summary>
    public static bool TryParseBattery(ReadOnlySpan<byte> p, out BatteryReading? left, out BatteryReading? right, out BatteryReading? @case)
    {
        left = right = @case = null;
        if (p.Length < 5 || p[0] != 0) return false;
        left = Reading(p[1], (p[4] & 0x01) != 0);
        right = Reading(p[2], (p[4] & 0x02) != 0);
        @case = Reading(p[3], (p[4] & 0x04) != 0);
        return true;

        static BatteryReading? Reading(byte level, bool charging) => level <= 100 ? new BatteryReading(level, charging) : null;
    }

    /// <summary>Noise report <c>[00][mode][reduce level]…</c>; mode 0 = noise cancelling, 1 = off, 2 = transparency.</summary>
    public static bool TryParseNoise(ReadOnlySpan<byte> p, out NoiseControlMode mode)
    {
        mode = NoiseControlMode.Off;
        if (p.Length < 2 || p[0] != 0) return false;
        switch (p[1])
        {
            case VivoConstants.NoiseModeAnc: mode = NoiseControlMode.NoiseCancellation; return true;
            case VivoConstants.NoiseModeOff: mode = NoiseControlMode.Off; return true;
            case VivoConstants.NoiseModeTransparency: mode = NoiseControlMode.Transparency; return true;
            default: return false;
        }
    }

    public static byte NoiseModeByte(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.NoiseCancellation => VivoConstants.NoiseModeAnc,
        NoiseControlMode.Transparency => VivoConstants.NoiseModeTransparency,
        _ => VivoConstants.NoiseModeOff,
    };

    /// <summary>Wear report <c>[00][flags]</c>: bit0/bit1 = left/right in case, bit2/bit3 = left/right worn.</summary>
    public static bool TryParseWear(ReadOnlySpan<byte> p, out WearStatus wear)
    {
        wear = WearStatus.Unknown;
        if (p.Length < 2 || p[0] != 0) return false;
        byte f = p[1];
        wear = new WearStatus(
            LeftInEar: (f & 0x04) != 0,
            RightInEar: (f & 0x08) != 0,
            LeftInCase: (f & 0x01) != 0,
            RightInCase: (f & 0x02) != 0,
            CaseLidOpen: null);
        return true;
    }

    /// <summary>Status + value report <c>[00][value]…</c> (sound effect, game mode, find state).</summary>
    public static bool TryParseValue(ReadOnlySpan<byte> p, out byte value)
    {
        value = 0;
        if (p.Length < 2 || p[0] != 0) return false;
        value = p[1];
        return true;
    }

    /// <summary>
    /// Firmware report: either a compact binary version (<c>00 01 02 03 04 05 09 0C</c> → 2.5.9, bytes 2/5/6)
    /// or a status byte followed by text (<c>00 "1.2"</c>). A <c>_suffix</c> is dropped like the official app does.
    /// </summary>
    public static string? ParseFirmware(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 || p[0] != 0) return null;
        var body = p[1..];
        int printable = 0;
        foreach (byte b in body)
        {
            if (b is >= 0x20 and <= 0x7E) printable++;
        }
        if (p.Length >= 7 && printable * 2 < body.Length)
        {
            byte major = p[2], minor = p[5], patch = p[6];
            return major <= 99 && minor <= 99 && patch <= 99 ? $"{major}.{minor}.{patch}" : null;
        }
        int start = 0;
        while (start < body.Length && body[start] < 0x20) start++;
        int end = body[start..].IndexOf((byte)0);
        var textBytes = end < 0 ? body[start..] : body.Slice(start, end);
        if (textBytes.IsEmpty) return null;
        foreach (byte b in textBytes)
        {
            if (b is < 0x20 or > 0x7E) return null;
        }
        string text = Encoding.ASCII.GetString(textBytes).Trim();
        int underscore = text.IndexOf('_');
        return underscore >= 0 ? text[..underscore] : text;
    }
}
