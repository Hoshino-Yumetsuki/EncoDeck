// SPDX-License-Identifier: GPL-3.0-only
using System.Text;

namespace EncoDeck.Core.Resources;

/// <summary>Matches locally so customized Bluetooth names never leave the PC.</summary>
public static partial class OfficialArtworkCatalog
{
    private sealed record Entry(string Id, string Brand, string Name);

    public static (string ModelId, string Name)? Lookup(DeviceResourceKey key)
    {
        bool BrandMatches(string brand) => key.Brand switch
        {
            "vivo" or "iqoo" => brand is "vivo" or "iqoo",
            "xiaomi" or "redmi" => brand is "xiaomi" or "redmi",
            _ => brand == key.Brand,
        };
        var direct = Entries.FirstOrDefault(e => e.Id == key.ModelId && BrandMatches(e.Brand));
        if (direct is not null) return (direct.Id, direct.Name);
        string device = Normalize(key.DeviceName), model = Normalize(key.ModelName);
        var named = Entries.FirstOrDefault(e => BrandMatches(e.Brand) &&
            (Normalize(e.Name) == device || Normalize(e.Name) == model));
        // Name-based resolution leaves aliases/finishes for the server to disambiguate.
        return named is null ? null : (string.Empty, named.Name);
    }

    private static string Normalize(string? name)
    {
        string value = new((name ?? string.Empty).Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        foreach (string brand in new[] { "HUAWEI", "EDIFIER" })
            if (value.StartsWith(brand, StringComparison.Ordinal)) return value[brand.Length..];
        return value;
    }
}
