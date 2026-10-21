// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EncoDeck.Core.Privacy;

/// <summary>
/// A 48-bit Bluetooth device address. Formatting defaults to a redacted form so that
/// full addresses never leak into logs or diagnostic reports by accident.
/// </summary>
public readonly record struct BluetoothAddress(ulong Value)
{
    /// <summary>Full address, e.g. <c>E4:98:E0:12:34:56</c>. Only use when the user explicitly opted in.</summary>
    public string ToFullString()
    {
        Span<char> buffer = stackalloc char[17];
        for (int i = 0; i < 6; i++)
        {
            byte b = (byte)(Value >> ((5 - i) * 8));
            b.TryFormat(buffer.Slice(i * 3, 2), out _, "X2", CultureInfo.InvariantCulture);
            if (i < 5) buffer[i * 3 + 2] = ':';
        }
        return new string(buffer);
    }

    /// <summary>Redacted address that keeps only the last two octets, e.g. <c>**:**:**:**:34:56</c>.</summary>
    public string ToRedactedString() => "**:**:**:**:" + ToFullString()[12..];

    /// <summary>
    /// Stable pseudonym for correlating one device across log lines without exposing its address.
    /// The salt should be per-installation so pseudonyms are not linkable between users.
    /// </summary>
    public string ToPseudonym(string salt)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + Value.ToString(CultureInfo.InvariantCulture)));
        return "dev-" + Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    public override string ToString() => ToRedactedString();
}
