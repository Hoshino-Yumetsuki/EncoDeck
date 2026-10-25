// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>
/// Constants of the EDIFIER SPP control protocol (as documented by mEDIFIER / its Klinkore fork,
/// via OppoPodsManager), with TWS state and GATT service facts from Edifier Connect 8.5.3.
/// </summary>
public static class EdifierConstants
{
    /// <summary>Fixed RFCOMM SPP service UUID used by EDIFIER headphones.</summary>
    public static readonly Guid SppServiceId = new("EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF");

    public static readonly Guid GattReadId = new("48090001-1a48-11e9-ab14-d663bd873d93");
    public static readonly Guid GattWriteId = new("48090002-1a48-11e9-ab14-d663bd873d93");

    public static bool IsGattService(Guid uuid)
    {
        string value = uuid.ToString();
        return value.StartsWith("4809", StringComparison.Ordinal) && value.EndsWith("-1a48-11e9-ab14-d663bd873d93", StringComparison.Ordinal);
    }

    public const byte RequestHead = 0xAA;
    public const byte ResponseHeadBb = 0xBB;
    public const byte ResponseHeadCc = 0xCC;

    /// <summary>Checksum seed: sum = 8217 + every byte before the checksum (big-endian, 2 bytes).</summary>
    public const ushort ChecksumSeed = 8217;

    /// <summary>Battery query; the response echoes the command with payload[0] = percent.</summary>
    public const byte QueryBattery = 0xD0;

    /// <summary>TWS state: [peer, leftPercent, rightPercent, casePercent?, flags?, ...].</summary>
    public const byte QueryTwsState = 0xF2;

    /// <summary>Noise query; the response payload is [mode, ambientVolume?].</summary>
    public const byte QueryNoiseMode = 0xCC;

    /// <summary>Noise set with payload [mode]; the device echoes the command as acknowledgement.</summary>
    public const byte SetNoiseMode = 0xC1;

    public const byte NoiseOff = 1;
    public const byte NoiseAnc = 2;
    public const byte NoiseTransparency = 3;
}
