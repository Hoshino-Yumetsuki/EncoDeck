// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>
/// Constants of the EDIFIER SPP control protocol (as documented by mEDIFIER / its Klinkore fork,
/// via OppoPodsManager). Only the RFCOMM (SPP) channel is supported; BLE-only models are not.
/// </summary>
public static class EdifierConstants
{
    /// <summary>Fixed RFCOMM SPP service UUID used by EDIFIER headphones.</summary>
    public static readonly Guid SppServiceId = new("EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF");

    public const byte RequestHead = 0xAA;
    public const byte ResponseHeadBb = 0xBB;
    public const byte ResponseHeadCc = 0xCC;

    /// <summary>Checksum seed: sum = 8217 + every byte before the checksum (big-endian, 2 bytes).</summary>
    public const ushort ChecksumSeed = 8217;

    /// <summary>Battery query; the response echoes the command with payload[0] = percent.</summary>
    public const byte QueryBattery = 0xD0;

    /// <summary>Noise query; the response payload is [mode, ambientVolume?].</summary>
    public const byte QueryNoiseMode = 0xCC;

    /// <summary>Noise set with payload [mode]; the device echoes the command as acknowledgement.</summary>
    public const byte SetNoiseMode = 0xC1;

    public const byte NoiseOff = 1;
    public const byte NoiseAnc = 2;
    public const byte NoiseTransparency = 3;
}
