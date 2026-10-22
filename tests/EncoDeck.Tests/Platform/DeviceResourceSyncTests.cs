// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Platform.Resources;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Platform;

public sealed class DeviceResourceSyncTests
{
    private sealed class Downloader : IDeviceResourceDownloader
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ResourceDownloadResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<ResourceDownloadProgress>? Progress;
        public Task<ResourceDownloadResult> EnsureAsync(DeviceResourceKey key, bool force = false, CancellationToken cancellationToken = default, IProgress<ResourceDownloadProgress>? progress = null)
        {
            Interlocked.Increment(ref Calls);
            Progress = progress;
            Started.TrySetResult();
            return Complete.Task.WaitAsync(cancellationToken);
        }
        public ResourceCacheInfo? GetCacheInfo(DeviceResourceKey key) => new(3, 4096, DateTimeOffset.UnixEpoch, "cache");
    }

    [Fact]
    public async Task DeviceUpdatesCoalesceDownloadsAndWaitForReportedColor()
    {
        var store = new DeviceStateStore();
        var download = new Downloader();
        await using var sync = new DeviceResourceSync(store, download, NullLogger<DeviceResourceSync>.Instance);
        sync.Start();
        var device = new DeviceIdentity("device", "Earbuds", new BluetoothAddress(0));
        store.Reset(DeviceState.Empty with { Device = device, ProductId = 0x06EC10 });
        Assert.Equal(0, download.Calls);
        store.TryUpdate(null, state => state with { ColorId = 2 });
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        for (int i = 0; i < 20; i++) store.TryUpdate(null, state => state with { LastResponseAt = DateTimeOffset.UtcNow });
        var pending = sync.RefreshAsync(new DeviceResourceKey("model", 0x06EC10, 2));
        Assert.Equal(1, download.Calls);
        download.Complete.SetResult(new ResourceDownloadResult(3, "cache", false));
        await pending;
        store.TryUpdate(null, state => state with { LastResponseAt = DateTimeOffset.UtcNow });
        Assert.Equal(1, download.Calls);
    }

    [Fact]
    public async Task StatusFollowsDownloaderProgressAndCompletion()
    {
        var store = new DeviceStateStore();
        var download = new Downloader();
        await using var sync = new DeviceResourceSync(store, download, NullLogger<DeviceResourceSync>.Instance);
        var events = new List<DeviceResourceStatus>();
        sync.StatusChanged += (_, status) => { lock (events) events.Add(status); };
        sync.Start();
        var device = new DeviceIdentity("device", "Earbuds", new BluetoothAddress(0));
        store.Reset(DeviceState.Empty with { Device = device, ProductId = 0x06EC10, ColorId = 2 });
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("Earbuds", sync.Status.DeviceName);
        Assert.Equal(0x06EC10u, sync.Status.Key!.ProductId);

        download.Progress!.Report(new ResourceDownloadProgress(ResourceSyncStage.Downloading, 2, 4, 512, 1024));
        Assert.Equal(ResourceSyncStage.Downloading, sync.Status.Stage);
        Assert.True(sync.Status.IsRunning);
        Assert.Equal((2, 4, 512L, (long?)1024), (sync.Status.Package, sync.Status.Packages, sync.Status.Received, sync.Status.Total));
        // Byte counts are throttled so the UI thread is not flooded; the final chunk of a package always arrives.
        int before;
        lock (events) before = events.Count;
        for (int i = 0; i < 1000; i++) download.Progress.Report(new ResourceDownloadProgress(ResourceSyncStage.Downloading, 2, 4, 513 + i % 500, 1024));
        lock (events) Assert.True(events.Count - before < 100, $"{events.Count - before} progress events");
        download.Progress.Report(new ResourceDownloadProgress(ResourceSyncStage.Downloading, 2, 4, 1024, 1024));
        Assert.Equal(1024, sync.Status.Received);

        download.Complete.SetResult(new ResourceDownloadResult(3, "cache", false));
        await sync.RefreshAsync(new DeviceResourceKey("model", 0x06EC10, 2));
        var done = sync.Status;
        Assert.Equal(ResourceSyncStage.Completed, done.Stage);
        Assert.False(done.IsRunning);
        Assert.Equal(3, done.Cache!.Files);
        Assert.Equal(4096, done.Cache.Bytes);
        lock (events) Assert.Contains(events, e => e.Stage == ResourceSyncStage.Checking);
    }

    private sealed class FailingDownloader(Exception error) : IDeviceResourceDownloader
    {
        public Task<ResourceDownloadResult> EnsureAsync(DeviceResourceKey key, bool force = false, CancellationToken cancellationToken = default, IProgress<ResourceDownloadProgress>? progress = null)
        {
            progress?.Report(new ResourceDownloadProgress(ResourceSyncStage.Checking));
            return Task.FromException<ResourceDownloadResult>(error);
        }
    }

    [Fact]
    public async Task PreviousDeviceCompletionCannotReplaceTheActiveArtworkStatus()
    {
        var store = new DeviceStateStore();
        var download = new Downloader();
        await using var sync = new DeviceResourceSync(store, download, NullLogger<DeviceResourceSync>.Instance);
        sync.Start();
        var device = new DeviceIdentity("first", "First earbuds", new BluetoothAddress(0));
        store.Reset(DeviceState.Empty with { Device = device, ProductId = 0x06EC10, ColorId = 1 });
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var old = sync.RefreshAsync(new DeviceResourceKey("model", 0x06EC10, 1));
        store.Reset(DeviceState.Empty with { Device = device with { Name = "Second earbuds" }, ProductId = 0x067410, ColorId = 1 });
        var current = sync.RefreshAsync(new DeviceResourceKey("model", 0x067410, 1));
        download.Complete.SetResult(new ResourceDownloadResult(3, "cache", false));
        await Task.WhenAll(old, current);
        Assert.Equal(0x067410u, sync.Status.Key!.ProductId);
        Assert.Equal("Second earbuds", sync.Status.DeviceName);
        Assert.Equal(ResourceSyncStage.Completed, sync.Status.Stage);
    }

    [Theory]
    [InlineData("offline", ResourceSyncFailure.Offline)]
    [InlineData("404", ResourceSyncFailure.NotAvailable)]
    [InlineData("502", ResourceSyncFailure.Server)]
    [InlineData("hash", ResourceSyncFailure.InvalidData)]
    [InlineData("unavailable", ResourceSyncFailure.NotAvailable)]
    [InlineData("disk", ResourceSyncFailure.Storage)]
    public async Task FailuresAreClassifiedForPlainLanguageMessages(string kind, ResourceSyncFailure expected)
    {
        Exception error = kind switch
        {
            "offline" => new HttpRequestException("No such host is known."),
            "404" => new HttpRequestException("Not found", null, System.Net.HttpStatusCode.NotFound),
            "502" => new HttpRequestException("Bad gateway", null, System.Net.HttpStatusCode.BadGateway),
            "hash" => new InvalidDataException("Resource SHA-256 verification failed."),
            "unavailable" => new ResourceUnavailableException("none"),
            _ => new IOException("Disk full"),
        };
        await using var sync = new DeviceResourceSync(new DeviceStateStore(), new FailingDownloader(error), NullLogger<DeviceResourceSync>.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => sync.RefreshAsync(new DeviceResourceKey("model", 0x06EC10, 2), force: true));
        Assert.Equal(ResourceSyncStage.Failed, sync.Status.Stage);
        Assert.Equal(expected, sync.Status.Failure);
        Assert.False(sync.Status.IsRunning);
    }

    [Fact]
    public async Task ShutdownCancelsPendingNetworkWork()
    {
        var download = new Downloader();
        var sync = new DeviceResourceSync(new DeviceStateStore(), download, NullLogger<DeviceResourceSync>.Instance);
        var task = sync.RefreshAsync(new DeviceResourceKey("model", 0x06EC10, 2));
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await sync.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
