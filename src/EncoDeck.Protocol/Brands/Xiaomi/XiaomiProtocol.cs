// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>Wire constants of the Xiaomi RCSP control channel, as far as the reference has confirmed them.</summary>
public static class XiaomiProtocol
{
    /// <summary>XIAOAI SPP service (non-standard base <c>…008584D01810</c>).</summary>
    public static readonly Guid XiaoAiSppUuid = new("00001101-0000-1000-8000-008584D01810");

    /// <summary>Type byte of requests.</summary>
    public const byte KindRequest = 0xC4;

    /// <summary>Type byte of the auth result report.</summary>
    public const byte KindAuthResult = 0x04;

    /// <summary>Battery query, and (with sequence <see cref="AuthSequence"/>) the auth challenge.</summary>
    public const byte OpTargetInfo = 0x02;

    public const byte OpAuthResult = 0x51;

    public const byte OpSetConfig = 0xF2;

    public const byte OpGetConfig = 0xF3;

    public const byte OpNotifyConfig = 0xF4;

    /// <summary>Sequence byte of the battery query (known-good capture <c>FE DC BA C4 02 00 05 0B FF FF FF FF EF</c>).</summary>
    public const byte BatterySequence = 0x0B;

    /// <summary>Sequence byte the reference uses for the auth challenge (CMD_AUTH_CHECK = 80).</summary>
    public const byte AuthSequence = 0x50;

    /// <summary>First sequence byte of config requests.</summary>
    public const byte ConfigSequenceStart = 0x90;

    public static ReadOnlySpan<byte> BatteryQueryPayload => [0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Battery answer prefix; the left/right/case levels follow.</summary>
    public static ReadOnlySpan<byte> BatteryResponsePrefix => [0x02, 0x02, 0x04, 0x07];

    /// <summary>Auth result report payload (<c>FE DC BA 04 51 00 03 00 03 01 EF</c>).</summary>
    public static ReadOnlySpan<byte> AuthResultPayload => [0x03, 0x01];

    /// <summary>Device config id of low-latency (game) mode; value 1 = on, 0 = off.</summary>
    public const ushort ConfigLowLatency = 47;

    /// <summary>Get-config payload: <c>[0x02][configId BE]</c>.</summary>
    public static byte[] GetConfigPayload(ushort configId) => [0x02, (byte)(configId >> 8), (byte)configId];

    /// <summary>Set-config payload: <c>[0x03][configId BE][value]</c>.</summary>
    public static byte[] SetConfigPayload(ushort configId, byte value) => [0x03, (byte)(configId >> 8), (byte)configId, value];

    /// <summary>Parses a config answer or notification <c>[0x03][configId BE][value]</c>.</summary>
    public static bool TryParseConfig(XiaomiFrame frame, out ushort configId, out byte value)
    {
        configId = 0;
        value = 0;
        if (frame.OpCode is not (OpGetConfig or OpNotifyConfig)) return false;
        var p = frame.Payload;
        if (p.Length < 4 || p[0] != 0x03) return false;
        configId = (ushort)((p[1] << 8) | p[2]);
        value = p[3];
        return true;
    }

    /// <summary>Parses the battery answer: left, right, case (each null when absent or out of range).</summary>
    public static bool TryParseBattery(XiaomiFrame frame, out int? left, out int? right, out int? @case)
    {
        left = right = @case = null;
        var p = frame.Payload;
        if (frame.OpCode != OpTargetInfo || p.Length < 5 || !p.AsSpan().StartsWith(BatteryResponsePrefix)) return false;
        left = Level(p, 4);
        right = Level(p, 5);
        @case = Level(p, 6);
        return true;

        static int? Level(byte[] p, int i) => i < p.Length && p[i] <= 100 ? p[i] : null;
    }
}
