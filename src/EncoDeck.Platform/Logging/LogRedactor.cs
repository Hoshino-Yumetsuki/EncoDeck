// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;

namespace EncoDeck.Platform.Logging;

/// <summary>
/// Removes Bluetooth addresses from free text before it reaches a log file or diagnostic report.
/// Keeps the last two octets so a user can still tell their own devices apart.
/// </summary>
public static partial class LogRedactor
{
    // aa:bb:cc:dd:ee:ff or aa-bb-cc-dd-ee-ff
    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9A-Fa-f]{2})[:\-]([0-9A-Fa-f]{2})[:\-]([0-9A-Fa-f]{2})[:\-]([0-9A-Fa-f]{2})[:\-]([0-9A-Fa-f]{2})[:\-]([0-9A-Fa-f]{2})(?![0-9A-Fa-f])")]
    private static partial Regex SeparatedAddress();

    // 12 contiguous hex digits as they appear in PnP ids (DEV_E498E03A2AA3, _E498E03A2AA3_C0...).
    [GeneratedRegex(@"(?<=DEV_|&|_)([0-9A-Fa-f]{8})([0-9A-Fa-f]{4})(?![0-9A-Fa-f])")]
    private static partial Regex PnpAddress();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        string result = SeparatedAddress().Replace(text, m => $"**:**:**:**:{m.Groups[5].Value}:{m.Groups[6].Value}");
        result = PnpAddress().Replace(result, m => "********" + m.Groups[2].Value);
        return result;
    }
}
