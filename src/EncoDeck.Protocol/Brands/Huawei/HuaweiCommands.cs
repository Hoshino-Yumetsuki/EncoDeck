// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>HUAWEI command numbers (<c>service &lt;&lt; 8 | command</c>) and well-known TLV values.</summary>
public static class HuaweiCommands
{
    /// <summary>HUAWEI earbuds expose their control protocol on the standard Serial Port Profile service.</summary>
    public static readonly Guid SppServiceUuid = new("00001101-0000-1000-8000-00805F9B34FB");

    /// <summary>Battery query and report: TLV 1 = overall level, 2 = [left, right, case], 3 = charging flags.</summary>
    public const ushort Battery = 0x0108;

    /// <summary>Unsolicited battery report sent by some models.</summary>
    public const ushort BatteryAlt = 0x0127;

    /// <summary>Noise-control state query and report: TLV 1 = [sub-mode, mode].</summary>
    public const ushort QueryAnc = 0x2B2A;

    /// <summary>Noise-control write: TLV 1 = [mode, sub-mode]; no acknowledgement, confirmed by <see cref="QueryAnc"/>.</summary>
    public const ushort SetAnc = 0x2B04;

    /// <summary>Wear (in-ear) detection write: TLV 1 = 0/1.</summary>
    public const ushort SetWearDetection = 0x2B10;

    /// <summary>Wear detection query/report: TLV 1 = 0/1.</summary>
    public const ushort QueryWearDetection = 0x2B11;

    /// <summary>EQ write: TLV 1 = preset id.</summary>
    public const ushort SetEqualizer = 0x2B49;

    /// <summary>EQ query/report: TLV 2 = selected preset id.</summary>
    public const ushort QueryEqualizer = 0x2B4A;

    /// <summary>Low-latency (game) mode, read and write: write TLV 1 = 0/1, read answer TLV 2 = 0/1.</summary>
    public const ushort LowLatency = 0x2B6C;

    /// <summary>Dual-connection master switch write: TLV 1 = 0/1.</summary>
    public const ushort SetDualConnect = 0x2B2E;

    /// <summary>Dual-connection master switch query: TLV 1 = 0/1.</summary>
    public const ushort QueryDualConnect = 0x2B2F;

    /// <summary>Official audio plugin MBB feature state; TLV 1 selects feature 24 (independent spatial audio).</summary>
    public const ushort FeatureState = 0x2BB4;

    /// <summary>TLV type a device uses to return an error code for a command.</summary>
    public const byte ErrorTlv = 0x7F;

    public const byte AncModeOff = 0x00;
    public const byte AncModeNoiseCancellation = 0x01;
    public const byte AncModeTransparency = 0x02;

    /// <summary>Sub-mode sent when a model has no discrete levels.</summary>
    public const byte AncSubModeDefault = 0xFF;
}
