// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.Paths;

/// <summary>Per-user storage locations. Nothing is written next to the executable.</summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EncoDeck");
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string DeviceCacheFile => Path.Combine(Root, "device-cache.json");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");

    /// <summary>Downloaded installers and the last update-check time.</summary>
    public string UpdatesDirectory => Path.Combine(Root, "Updates");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
