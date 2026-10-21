// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Packaging;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace EncoDeck.Platform.Startup;

/// <summary>
/// Per-user "start with Windows": HKCU\...\Run for the unpackaged build, the manifest's
/// StartupTask (<see cref="TaskId"/>) for the MSIX build.
/// </summary>
public sealed class StartupRegistration(string valueName = "EncoDeck")
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Argument appended so the app starts minimized to the tray.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>TaskId of the windows.startupTask extension in Package.appxmanifest.</summary>
    public const string TaskId = "EncoDeckStartup";

    public bool IsEnabled(string executablePath)
    {
        if (PackageIdentity.IsPackaged)
        {
            return GetTask().State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) is string value
            && value.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string executablePath, bool enabled)
    {
        if (PackageIdentity.IsPackaged)
        {
            var task = GetTask();
            if (enabled) task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            else task.Disable();
            return;
        }
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

    private static StartupTask GetTask() => StartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult();
}
