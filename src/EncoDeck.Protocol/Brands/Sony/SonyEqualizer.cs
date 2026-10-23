// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>Official MDR table 1 EQ capability and parameter layouts (types 0, 2 and 4).</summary>
internal static class SonyEqualizer
{
    internal static bool TryPresets(ReadOnlySpan<byte> p, out IReadOnlyList<EqualizerPreset> presets, bool legacy = false)
    {
        presets = [];
        if (p.Length < 5 || p[0] != 0x51 || !(legacy ? p[1] is 1 or 3 : p[1] is 0 or 2 or 4)) return false;
        int count = p[4], offset = 5;
        var result = new List<EqualizerPreset>(); var ids = new HashSet<byte>();
        for (int i = 0; i < count; i++)
        {
            if (offset + 2 > p.Length) return false;
            byte id = p[offset]; int length = p[offset + 1];
            if (length > 128 || offset + 2 + length > p.Length || !ids.Add(id) || id == 255) return false;
            string name;
            try { name = new UTF8Encoding(false, true).GetString(p.Slice(offset + 2, length)); }
            catch (DecoderFallbackException) { return false; }
            if (name.Any(char.IsControl)) return false;
            result.Add(new($"sony-{id}", string.IsNullOrWhiteSpace(name) ? $"EQ {id}" : name, "", CapabilitySupport.Supported));
            offset += length + 2;
        }
        if (offset != p.Length || result.Count == 0) return false;
        presets = result; return true;
    }

    internal static bool TryParameter(ReadOnlySpan<byte> p, out byte preset, bool legacy = false)
    {
        preset = 0;
        if (p.Length < 4 || p[0] is not (0x57 or 0x59) || !(legacy ? p[1] is 1 or 3 : p[1] is 0 or 2 or 4) || p.Length != 4 + p[3] || p[2] == 255) return false;
        preset = p[2]; return true;
    }
}
