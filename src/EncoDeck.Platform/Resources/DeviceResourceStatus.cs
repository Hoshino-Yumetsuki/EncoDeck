// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Text.Json;
using EncoDeck.Core.Resources;

namespace EncoDeck.Platform.Resources;

public enum ResourceSyncStage
{
    Idle,
    /// <summary>Asking EncoAPI which packages belong to the product and color.</summary>
    Checking,
    Downloading,
    Extracting,
    /// <summary>New files were published.</summary>
    Completed,
    /// <summary>The local cache is still fresh; nothing was downloaded.</summary>
    UpToDate,
    Failed,
}

/// <summary>Why a download failed, in categories the UI can explain without exception text.</summary>
public enum ResourceSyncFailure
{
    None,
    /// <summary>No connection, DNS failure or timeout.</summary>
    Offline,
    /// <summary>EncoAPI or the supplier answered with an error status.</summary>
    Server,
    /// <summary>EncoAPI has no artwork for this product and color.</summary>
    NotAvailable,
    /// <summary>Checksum mismatch, unsafe archive or malformed metadata.</summary>
    InvalidData,
    /// <summary>The cache folder could not be written.</summary>
    Storage,
    Other,
}

/// <summary>
/// One step reported by <see cref="IDeviceResourceDownloader.EnsureAsync"/>. <see cref="Package"/> is
/// 1-based; <see cref="Total"/> is null when the server sent no Content-Length.
/// </summary>
public readonly record struct ResourceDownloadProgress(ResourceSyncStage Stage, int Package = 0, int Packages = 0, long Received = 0, long? Total = null);

/// <summary>What is cached for one product/color: resolved files, bytes on disk and the manifest time.</summary>
public sealed record ResourceCacheInfo(int Files, long Bytes, DateTimeOffset? UpdatedAt, string Folder);

/// <summary>Raised when EncoAPI has nothing to show for the product and color.</summary>
public sealed class ResourceUnavailableException(string message) : Exception(message);

/// <summary>Snapshot of the artwork download for the active device, for the settings page.</summary>
public sealed record DeviceResourceStatus
{
    public static DeviceResourceStatus Idle { get; } = new();

    public DeviceResourceKey? Key { get; init; }

    public string? DeviceName { get; init; }

    public ResourceSyncStage Stage { get; init; }

    public int Package { get; init; }

    public int Packages { get; init; }

    public long Received { get; init; }

    public long? Total { get; init; }

    public ResourceSyncFailure Failure { get; init; }

    public ResourceCacheInfo? Cache { get; init; }

    public bool IsRunning => Stage is ResourceSyncStage.Checking or ResourceSyncStage.Downloading or ResourceSyncStage.Extracting;

    public static ResourceSyncFailure Classify(Exception exception) => exception switch
    {
        ResourceUnavailableException => ResourceSyncFailure.NotAvailable,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => ResourceSyncFailure.NotAvailable,
        HttpRequestException { StatusCode: HttpStatusCode.Conflict } => ResourceSyncFailure.NotAvailable,
        HttpRequestException { StatusCode: null } => ResourceSyncFailure.Offline,
        HttpRequestException => ResourceSyncFailure.Server,
        // A timeout (shutdown is filtered out by the caller).
        OperationCanceledException => ResourceSyncFailure.Offline,
        InvalidDataException or JsonException => ResourceSyncFailure.InvalidData,
        IOException or UnauthorizedAccessException => ResourceSyncFailure.Storage,
        _ => ResourceSyncFailure.Other,
    };
}
