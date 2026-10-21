// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Extensions.Logging;
using Windows.Services.Store;

namespace EncoDeck.Platform.Updates;

/// <summary>
/// Updates for the Microsoft Store (MSIX) build, through the Store itself. The installer build uses
/// <see cref="UpdateManager"/> and GitHub Releases instead.
/// </summary>
public sealed class StoreUpdateService(ILogger<StoreUpdateService> logger)
{
    private StoreContext? _context;

    /// <summary>Number of package updates the Store offers for this app (0 when up to date).</summary>
    public async Task<int> CheckAsync()
    {
        var updates = await Context.GetAppAndOptionalStorePackageUpdatesAsync();
        logger.LogInformation("Store reports {Count} package update(s)", updates.Count);
        return updates.Count;
    }

    /// <summary>
    /// Downloads and installs the pending updates with the Store's own consent UI, parented to
    /// <paramref name="ownerWindow"/>. Windows restarts the app once the update is installed.
    /// </summary>
    public async Task<bool> InstallAsync(nint ownerWindow)
    {
        var context = Context;
        if (ownerWindow != 0) WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindow);
        var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
        if (updates.Count == 0) return false;
        var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
        logger.LogInformation("Store update finished: {State}", result.OverallState);
        return result.OverallState == StorePackageUpdateState.Completed;
    }

    private StoreContext Context => _context ??= StoreContext.GetDefault();
}
