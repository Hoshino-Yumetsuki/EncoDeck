// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Paths;
using EncoDeck.Platform.Updates;
namespace EncoDeck.App.Services;
internal static class LhdcCompanion
{
    private static readonly AppPaths Paths = new();

    /// <summary>The LHDC component release pinned by this app; independent of the app version.</summary>
    public static ComponentInstaller Component { get; } = LhdcComponent.CreateInstaller(new HttpClient(), Path.Combine(Paths.Root, "Components", "LHDC"));

    public static string? RuntimeDirectory => Component.Installed?.Directory ?? BundledRuntimeDirectory();

    /// <summary>An offline installer (build-installer.ps1 -WithLhdc) puts the runtime next to the app.</summary>
    private static string? BundledRuntimeDirectory()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "LHDC");
        return LhdcComponent.IsRuntime(directory) ? directory : null;
    }

    public static string StatusKey => PackageIdentity.IsPackaged ? "Lhdc_Store"
        : RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64 ? "Lhdc_Architecture"
        : !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ? "Lhdc_Windows"
        : RuntimeDirectory is not null ? "Lhdc_Ready"
        : Component.CanInstall ? "Lhdc_Missing" : "Lhdc_Unpublished";
    public static bool IsReady => StatusKey == "Lhdc_Ready";

    /// <summary>Installed, but this app pins a newer build (the installed one keeps working meanwhile).</summary>
    public static bool UpdateAvailable => IsReady && Component.CanInstall && Component.Installed is not null && !Component.IsCurrent;

    public static bool CanDownload => StatusKey == "Lhdc_Missing" || UpdateAvailable;

    /// <summary>Download size of the pinned package in MB.</summary>
    public static double PackageMegabytes => (Component.Package?.Size ?? 0) / 1048576.0;

    public static Task DownloadAsync(IProgress<UpdateDownloadProgress> progress, CancellationToken ct) => Component.InstallAsync(progress, ct);

    public static LhdcBackend Backend => IsReady
        ? new(RuntimeDirectory!, Path.Combine(Component.Root, LhdcComponent.ResultsFolder))
        : throw new InvalidOperationException(Loc.Get(StatusKey));
}
