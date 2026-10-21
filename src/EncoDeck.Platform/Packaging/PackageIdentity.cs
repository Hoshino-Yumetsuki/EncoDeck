// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace EncoDeck.Platform.Packaging;

/// <summary>
/// Whether this process runs with MSIX package identity (the Microsoft Store build). The packaged build
/// starts with Windows through a StartupTask and is updated by the Store instead of GitHub Releases.
/// </summary>
public static partial class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
