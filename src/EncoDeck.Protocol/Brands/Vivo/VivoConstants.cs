// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>
/// vivo / iQOO compact GAIA command ids. A report or acknowledgement is the request id | 0x8000;
/// a query is the set id + 0x0100 (e.g. set 0x0130, query 0x0230, report 0x8230).
/// </summary>
internal static class VivoCommands
{
    /// <summary>Version negotiation; always GAIA vendor 0x000A and version 4.</summary>
    public const ushort Handshake = 0x0300;
    public const ushort HandshakeResponse = 0x8300;

    /// <summary>Empty frames 0x0202–0x0206 sent after the handshake make the earbuds start pushing reports.</summary>
    public static readonly ushort[] RegisterNotifications = [0x0202, 0x0203, 0x0204, 0x0205, 0x0206];

    /// <summary>Battery: report payload <c>[00][left%][right%][case%][charging bits L=1 R=2 case=4]</c>. Always GAIA version 4.</summary>
    public const ushort QueryBattery = 0x0207;
    public const ushort ReportBattery = 0x8207;

    /// <summary>Current noise mode: set payload <c>[mode, ..model suffix]</c>; report <c>[00][mode][reduce level]</c>.</summary>
    public const ushort SetNoiseMode = 0x0130;
    public const ushort AckNoiseMode = 0x8130;
    public const ushort QueryNoiseMode = 0x0230;
    public const ushort ReportNoiseMode = 0x8230;

    /// <summary>Find earbuds: set <c>[01]</c> ring / <c>[00]</c> stop; ack <c>[00][state]</c>.</summary>
    public const ushort SetFindDevice = 0x0120;
    public const ushort AckFindDevice = 0x8120;

    /// <summary>Live wear state: report <c>[00][flags]</c>, bit0/1 = left/right in case, bit2/3 = left/right worn.</summary>
    public const ushort QueryWearState = 0x020D;
    public const ushort ReportWearState = 0x820D;

    /// <summary>Built-in sound effect (device EQ preset): set <c>[id]</c>; ack/report <c>[00][id]</c>.</summary>
    public const ushort SetAudioEffect = 0x0118;
    public const ushort AckAudioEffect = 0x8118;
    public const ushort QueryAudioEffect = 0x0218;
    public const ushort ReportAudioEffect = 0x8218;

    /// <summary>Low-latency game mode: set <c>[0|1]</c>; ack/report <c>[00][state]</c>.</summary>
    public const ushort SetLowLatency = 0x0151;
    public const ushort AckLowLatency = 0x8151;
    public const ushort QueryLowLatency = 0x0251;
    public const ushort ReportLowLatency = 0x8251;

    /// <summary>Firmware version query.</summary>
    public const ushort QueryFirmware = 0x021C;
    public const ushort ReportFirmware = 0x821C;

    /// <summary>The earbuds ask for the phone's local time; answered with <see cref="HostTimeResponse"/>.</summary>
    public const ushort PeerTimeRequest = 0x8509;
    public const ushort HostTimeResponse = 0x0509;
}

/// <summary>Framing constants and the control service.</summary>
internal static class VivoConstants
{
    /// <summary>vivo private RFCOMM (GAIA) service.</summary>
    public static readonly Guid ServiceUuid = new("00000837-D102-11E1-9B23-00025B00A5A5");

    public const byte Preamble = 0xFF;
    public const ushort GaiaVendor = 0x000A;
    public const ushort VivoVendor = 0x001B;
    public const byte HandshakeVersion = 4;
    public const int HeaderLength = 8;
    public const int MaxPayload = 254;

    /// <summary>Noise mode bytes of <see cref="VivoCommands.SetNoiseMode"/> (same on every model).</summary>
    public const byte NoiseModeAnc = 0x00;
    public const byte NoiseModeOff = 0x01;
    public const byte NoiseModeTransparency = 0x02;

    /// <summary>DeepX custom sound effect id; not a built-in preset.</summary>
    public const byte DeepXCustomEffect = 0x10;
}
