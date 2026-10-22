// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Text.Json;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Paths;
using EncoDeck.Platform.Updates;
namespace EncoDeck.App.Services;
internal static class LhdcCompanion
{
    private static readonly AppPaths Paths = new();
    private static readonly LhdcDownloadService Downloads = new(new HttpClient(), Path.Combine(Paths.Root, "Components", "LHDC"));
    public static string? RuntimeDirectory => Downloads.InstalledDirectory
        ?? BundledRuntimeDirectory();
    private static string? BundledRuntimeDirectory()
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "LHDC");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "runtime.json")));
            return manifest.RootElement.GetProperty("EncoDeckVersion").GetString() == LhdcDownloadService.ApplicationVersion ? directory : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { return null; }
    }
    public static string StatusKey => PackageIdentity.IsPackaged ? "Lhdc_Store"
        : RuntimeInformation.OSArchitecture != Architecture.X64 || RuntimeInformation.ProcessArchitecture != Architecture.X64 ? "Lhdc_Architecture"
        : !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ? "Lhdc_Windows"
        : RuntimeDirectory is null ? "Lhdc_Missing" : "Lhdc_Ready";
    public static bool IsReady => StatusKey == "Lhdc_Ready";
    public static bool CanDownload => StatusKey == "Lhdc_Missing";
    public static Task DownloadAsync(IProgress<UpdateDownloadProgress> progress, CancellationToken ct) => Downloads.DownloadAsync(progress, ct);
    public static LhdcBackend Backend => IsReady
        ? new(RuntimeDirectory!, Path.Combine(Paths.Root, "Components", "LHDC", "results"))
        : throw new InvalidOperationException(Loc.Get(StatusKey));
}
