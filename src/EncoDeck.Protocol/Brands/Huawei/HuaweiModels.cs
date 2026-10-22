// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using System.Text;

namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>One discrete noise-cancelling level and its protocol sub-mode.</summary>
public sealed record HuaweiAncLevel(string LevelId, byte SubMode, string DisplayName, string ResourceKey);

/// <summary>One built-in EQ preset and its protocol id.</summary>
public sealed record HuaweiEqPreset(byte ProtocolId, string PresetId, string DisplayName, string ResourceKey);

/// <summary>
/// What a HUAWEI model supports, as the reference's per-model table declares it (aligned with
/// OpenFreebuds / HuaweiPods; not verified on hardware by EncoDeck).
/// </summary>
public sealed record HuaweiModel(
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    bool Anc,
    bool Transparency,
    bool AncReadback,
    IReadOnlyList<HuaweiAncLevel> AncLevels,
    bool RfcommBattery,
    bool WearDetection,
    IReadOnlyList<HuaweiEqPreset> EqPresets,
    bool LowLatency,
    bool DualConnect,
    bool HasChargingCase,
    byte TransparencySubMode = HuaweiCommands.AncSubModeDefault,
    byte PreferredSppChannel = 0,
    HuaweiProtocol Protocol = HuaweiProtocol.Legacy,
    bool IsGeneric = false)
{
    /// <summary>Noise control is offered only where the state can be read back to confirm a write.</summary>
    public bool NoiseControlUsable => Anc && AncReadback;
}

/// <summary>Built-in HUAWEI model table and name matching.</summary>
public static class HuaweiModels
{
    private static HuaweiAncLevel Dynamic => new("dynamic", 0x03, "Dynamic", "Huawei_NoiseLevel_Dynamic");
    private static HuaweiAncLevel Cozy => new("cozy", 0x01, "Cozy", "Huawei_NoiseLevel_Cozy");
    private static HuaweiAncLevel General => new("general", 0x00, "General", "Huawei_NoiseLevel_General");
    private static HuaweiAncLevel Ultra => new("ultra", 0x02, "Ultra", "Huawei_NoiseLevel_Ultra");

    // OpenFreebuds cancel_level_options: comfort = 1, normal = 0, ultra = 2, dynamic = 3.
    private static readonly HuaweiAncLevel[] FullLevels = [Dynamic, Cozy, General, Ultra];
    private static readonly HuaweiAncLevel[] NoDynamicLevels = [Cozy, General, Ultra];
    private static readonly HuaweiAncLevel[] FreeBuds5Levels = [Dynamic, Cozy, General];
    private static readonly HuaweiAncLevel[] FreeBuds4ELevels = [Cozy, General];

    private static HuaweiEqPreset Eq(byte id, string presetId, string name, string key) => new(id, presetId, name, "Huawei_Eq_" + key);

    // Pro 3 / 4 / 5 use different ids from the other models (OpenFreebuds buds_pro_3.py / buds_pro_5.py).
    private static readonly HuaweiEqPreset[] ProEq =
        [Eq(5, "default", "Default", "Default"), Eq(1, "bass", "Bass boost", "Bass"), Eq(2, "treble", "Treble boost", "Treble"), Eq(9, "voices", "Vocals", "Voices")];
    private static readonly HuaweiEqPreset[] ThreeEq =
        [Eq(1, "default", "Default", "Default"), Eq(2, "bass", "Bass boost", "Bass"), Eq(3, "treble", "Treble boost", "Treble")];
    private static readonly HuaweiEqPreset[] StandardEq = [.. ThreeEq, Eq(9, "voices", "Vocals", "Voices")];

    /// <summary>Every built-in model (24).</summary>
    public static IReadOnlyList<HuaweiModel> All { get; } =
    [
        M("huawei-freebuds-3", "HUAWEI FreeBuds 3", anc: true, readback: false, battery: false, hasCase: true),
        M("huawei-freebuds-3i", "HUAWEI FreeBuds 3i", anc: true, transparency: true, readback: true, battery: true, hasCase: true),
        M("huawei-freebuds-4e", "HUAWEI FreeBuds 4E", anc: true, readback: true, levels: FreeBuds4ELevels, battery: true, wear: true, hasCase: true),
        M("huawei-freebuds-4i", "HUAWEI FreeBuds 4i", anc: true, transparency: true, readback: true, battery: true, wear: true, hasCase: true),
        M("huawei-freebuds-5", "HUAWEI FreeBuds 5", anc: true, readback: true, levels: FreeBuds5Levels, battery: true, wear: true, lowLatency: true, hasCase: true),
        M("huawei-freebuds-5i", "HUAWEI FreeBuds 5i", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: StandardEq, lowLatency: true, dual: true, hasCase: true),
        M("huawei-freebuds-6i", "HUAWEI FreeBuds 6i", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: StandardEq, lowLatency: true, dual: true, hasCase: true, transparencySub: 0x02, channel: 1),
        M("huawei-freebuds-7i", "HUAWEI FreeBuds 7i", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: StandardEq, hasCase: true),
        M("huawei-freebuds-pro", "HUAWEI FreeBuds Pro", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-pro-2", "HUAWEI FreeBuds Pro 2", anc: true, transparency: true, readback: true, levels: NoDynamicLevels, battery: true, wear: true, eq: ThreeEq, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-pro-3", "HUAWEI FreeBuds Pro 3", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: ProEq, lowLatency: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-pro-4", "HUAWEI FreeBuds Pro 4", anc: true, readback: false, battery: true, wear: true, eq: ProEq, lowLatency: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-pro-5", "HUAWEI FreeBuds Pro 5", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: ProEq, lowLatency: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freebuds-se", "HUAWEI FreeBuds SE", battery: true, hasCase: true),
        M("huawei-freebuds-se-2", "HUAWEI FreeBuds SE 2", battery: true, eq: StandardEq, lowLatency: true, hasCase: true, channel: 1),
        M("huawei-freebuds-se-4", "HUAWEI FreeBuds SE 4", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, eq: StandardEq, lowLatency: true, hasCase: true, channel: 1),
        M("huawei-freebuds-studio", "HUAWEI FreeBuds Studio", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, wear: true, eq: ThreeEq, hasCase: false, channel: 1),
        M("huawei-freeclip", "HUAWEI FreeClip", battery: true, hasCase: true),
        M("huawei-freeclip-2", "HUAWEI FreeClip 2", battery: true, wear: true, eq: StandardEq, lowLatency: true, dual: true, hasCase: true, channel: 1),
        M("huawei-freearc", "HUAWEI FreeArc", battery: true, eq: StandardEq, hasCase: true),
        M("huawei-freelace-pro", "HUAWEI FreeLace Pro", anc: true, transparency: true, readback: true, levels: NoDynamicLevels, battery: true, hasCase: false),
        // FreeLace Pro 2 only has custom EQ (no built-in presets), which EncoDeck does not port.
        M("huawei-freelace-pro-2", "HUAWEI FreeLace Pro 2", anc: true, transparency: true, readback: true, levels: FullLevels, battery: true, lowLatency: true, dual: true, hasCase: false, channel: 1),
        M("huawei-eyewear", "HUAWEI Eyewear", battery: true, hasCase: false, shortAlias: false),
        M("huawei-eyewear-2", "HUAWEI Eyewear 2", battery: true, lowLatency: true, hasCase: false),
    ];

    /// <summary>Name-only fallback for unrecognized HUAWEI audio devices: battery only.</summary>
    public static HuaweiModel Generic { get; } = new(
        "huawei-generic", "HUAWEI earbuds", [], Anc: false, Transparency: false, AncReadback: false, AncLevels: [],
        RfcommBattery: true, WearDetection: false, EqPresets: [], LowLatency: false, DualConnect: false, HasChargingCase: true, IsGeneric: true);

    private static readonly string[] FamilyPrefixes = ["huawei", "freebuds", "freeclip", "freearc", "freelace", "eyewear"];

    private static readonly (string Alias, HuaweiModel Model)[] AliasTable =
        [.. All.SelectMany(m => m.Aliases.Select(a => (Normalize(a), m))).OrderByDescending(x => x.Item1.Length)];

    /// <summary>Lower-case letters and digits only (the reference's <c>normalizeDeviceName</c>).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Exact alias match first, then the longest alias the name starts with (e.g. "FreeBuds Pro 3 (A1B2)").</summary>
    public static HuaweiModel? Detect(string? deviceName, out bool exact)
    {
        exact = false;
        string normalized = Normalize(deviceName);
        if (normalized.Length == 0) return null;
        foreach (var (alias, model) in AliasTable)
        {
            if (alias == normalized)
            {
                exact = true;
                return model;
            }
        }
        foreach (var (alias, model) in AliasTable)
        {
            if (normalized.StartsWith(alias, StringComparison.Ordinal)) return model;
        }
        return null;
    }

    /// <summary>Whether the name looks like any HUAWEI audio product family.</summary>
    public static bool IsFamilyName(string? deviceName)
    {
        string normalized = Normalize(deviceName);
        return normalized.Length > 0 && FamilyPrefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal));
    }

    private static HuaweiModel M(
        string id,
        string name,
        bool anc = false,
        bool transparency = false,
        bool readback = false,
        HuaweiAncLevel[]? levels = null,
        bool battery = false,
        bool wear = false,
        HuaweiEqPreset[]? eq = null,
        bool lowLatency = false,
        bool dual = false,
        bool hasCase = true,
        byte transparencySub = HuaweiCommands.AncSubModeDefault,
        byte channel = 0,
        bool shortAlias = true)
    {
        string shortName = name["HUAWEI ".Length..];
        string[] aliases = shortAlias ? [name, shortName] : [name];
        return new HuaweiModel(id, name, aliases, anc, transparency, readback, levels ?? [], battery, wear, eq ?? [], lowLatency, dual, hasCase, transparencySub, channel);
    }
}
