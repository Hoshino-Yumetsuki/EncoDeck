// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using EncoDeck.Platform.Interop;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace EncoDeck.Platform.Updates;

/// <summary>
/// Updates for the Microsoft Store (MSIX) build, through the Store itself. The installer build uses
/// <see cref="UpdateManager"/> and GitHub Releases instead.
/// </summary>
public sealed class StoreUpdateService(ILogger<StoreUpdateService> logger)
{
    /// <summary>Restart only for updates: not after a crash or hang, and not after a reboot (the startup task covers that).</summary>
    private const uint RestartFlags = 0x1 | 0x2 | 0x8;

    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(8);

    private StoreContext? _context;
    private PackageCatalog? _catalog;
    private int _updateStarting;
    private volatile bool _installing;

    /// <summary>
    /// The Store (its own app, an automatic update, or another tool) is replacing this package while it runs.
    /// Raised once, on a background thread; the app should exit so the update is not blocked as "in use".
    /// Not raised while <see cref="InstallAsync"/> runs, since Windows closes and restarts the app itself then.
    /// </summary>
    public event EventHandler? UpdateStarting;

    /// <summary>Number of package updates the Store offers for this app (0 when up to date).</summary>
    public async Task<int> CheckAsync()
    {
        var updates = await Context.GetAppAndOptionalStorePackageUpdatesAsync();
        if (updates.Count == 0)
        {
            // The first query often answers from the Store client's cached catalog and only starts its refresh.
            await Task.Delay(RecheckDelay);
            updates = await Context.GetAppAndOptionalStorePackageUpdatesAsync();
        }
        logger.LogInformation("Store reports {Count} package update(s)", updates.Count);
        return updates.Count;
    }

    /// <summary>
    /// The listing's "What's new in this version" text, in the Store's display language; null when the
    /// listing has none or the Store cannot be reached.
    /// </summary>
    public async Task<string?> GetReleaseNotesAsync()
    {
        try
        {
            var result = await Context.GetStoreProductForCurrentAppAsync();
            if (result.ExtendedError is { } error) logger.LogInformation("Store listing unavailable (0x{Result:X8})", error.HResult);
            var product = result.Product;
            if (product is null) return null;
            return FindReleaseNotes(product.ExtendedJsonData)
                ?? product.Skus.Select(s => FindReleaseNotes(s.ExtendedJsonData)).FirstOrDefault(n => n is not null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read the Store release notes ({Reason})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The first non-empty "ReleaseNotes" in catalog JSON (it sits in each SKU's LocalizedProperties), as <see cref="ReleaseNotes.ToPlainText"/>.</summary>
    internal static string? FindReleaseNotes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReleaseNotes.ToPlainText(Find(document.RootElement));
        }
        catch (JsonException) { return null; }

        static string? Find(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("ReleaseNotes") && property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        return property.Value.GetString();
                    if (Find(property.Value) is { } found) return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    if (Find(item) is { } found) return found;
            }
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the pending updates with the Store's own consent UI, parented to
    /// <paramref name="ownerWindow"/>. Windows closes the app for the install and, through
    /// <see cref="RegisterRestart"/>, starts it again.
    /// </summary>
    public async Task<bool> InstallAsync(nint ownerWindow)
    {
        var context = Context;
        if (ownerWindow != 0) WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindow);
        var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
        if (updates.Count == 0) return false;
        _installing = true;
        try
        {
            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            logger.LogInformation("Store update finished: {State}", result.OverallState);
            return result.OverallState == StorePackageUpdateState.Completed;
        }
        finally { _installing = false; }
    }

    /// <summary>Asks Windows to start the app again with <paramref name="arguments"/> when an update closes it.</summary>
    public void RegisterRestart(string arguments)
    {
        int hr = NativeMethods.RegisterApplicationRestart(arguments, RestartFlags);
        if (hr != 0) logger.LogWarning("RegisterApplicationRestart failed (0x{Result:X8})", hr);
    }

    /// <summary>Starts watching for a newer version of this package being staged or installed; see <see cref="UpdateStarting"/>.</summary>
    public void WatchForUpdates()
    {
        if (_catalog is not null) return;
        try
        {
            _catalog = PackageCatalog.OpenForCurrentPackage();
            _catalog.PackageStaging += (_, e) =>
            {
                if (e.IsComplete && e.ErrorCode is null) OnPackageChanging(e.Package, "staged");
            };
            _catalog.PackageUpdating += (_, e) => OnPackageChanging(e.TargetPackage, "updating");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not watch for Store updates ({Reason})", ex.GetType().Name);
        }
    }

    private void OnPackageChanging(Package? target, string phase)
    {
        try
        {
            var current = Package.Current.Id;
            if (target is null || target.Id.FamilyName != current.FamilyName || ToVersion(target.Id.Version) <= ToVersion(current.Version)) return;
            logger.LogInformation("Store update to {Version} {Phase}{Installing}", ToVersion(target.Id.Version), phase, _installing ? " (requested in the app)" : string.Empty);
            if (_installing || Interlocked.Exchange(ref _updateStarting, 1) != 0) return;
            UpdateStarting?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Store update notification failed ({Reason})", ex.GetType().Name);
        }
    }

    private static Version ToVersion(PackageVersion v) => new(v.Major, v.Minor, v.Build, v.Revision);

    private StoreContext Context => _context ??= StoreContext.GetDefault();
}
