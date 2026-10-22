// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Packaging;

namespace EncoDeck.Platform.Paths;

/// <summary>Per-user storage locations. Nothing is written next to the executable.</summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? (PackageIdentity.IsPackaged ? PackagedRoot() : LegacyRoot);
    }

    private static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EncoDeck");

    /// <summary>
    /// The package's own LocalState. %LOCALAPPDATA% is virtualized for MSIX apps and directory
    /// renames there fail with IOException, which broke the atomic artwork cache publish.
    /// </summary>
    private static string PackagedRoot()
    {
        string root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        // 1.0.0 wrote to the virtualized folder; carry settings and the device cache over once.
        try
        {
            foreach (string name in new[] { "settings.json", "device-cache.json" })
            {
                string source = Path.Combine(LegacyRoot, name), target = Path.Combine(root, name);
                if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return root;
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
