// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Win32;

namespace EncoDeck.Platform.Startup;

/// <summary>
/// Per-user "start with Windows" for the unpackaged build, via HKCU\...\Run.
/// (An MSIX build would use a StartupTask extension instead.)
/// </summary>
public sealed class StartupRegistration(string valueName = "EncoDeck")
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Argument appended so the app starts minimized to the tray.</summary>
    public const string BackgroundArgument = "--background";

    public bool IsEnabled(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) is string value
            && value.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string executablePath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(valueName, $"\"{executablePath}\" {BackgroundArgument}", RegistryValueKind.String);
        }
        else if (key.GetValue(valueName) is not null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
