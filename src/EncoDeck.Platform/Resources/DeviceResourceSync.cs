// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Resources;

/// <summary>Downloads for the active product/color in the background without blocking Bluetooth or UI events.</summary>
public sealed class DeviceResourceSync(IDeviceStateStore store, IDeviceResourceDownloader downloader, ILogger<DeviceResourceSync> logger) : IAsyncDisposable
{
    /// <summary>Byte progress is published at most this often; stage and package changes always are.</summary>
    private const long ProgressIntervalMs = 100;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, int?), Task<ResourceDownloadResult>> _pending = [];
    private readonly Dictionary<(string, int?), DateTimeOffset> _nextAttempt = [];
    private DeviceResourceStatus _status = DeviceResourceStatus.Idle;
    private bool _started;
    private bool _disposed;

    /// <summary>The latest status for the active device (or the last refreshed key).</summary>
    public DeviceResourceStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Raised on a background thread; handlers should read <see cref="Status"/> on their own thread.</summary>
    public event EventHandler<DeviceResourceStatus>? StatusChanged;

    public void Start()
    {
        if (_started) return;
        _started = true;
        store.StateChanged += OnStateChanged;
        Schedule(store.Current);
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e) => Schedule(e.Current);

    private void Schedule(DeviceState state)
    {
        if (state.Device is null || DeviceResourceKey.FromState(state) is not { CanDownload: true } key) return;
        bool newKey;
        lock (_gate)
        {
            newKey = !Same(_status.Key, key) || _status.DeviceName != state.Device.Name;
            if (newKey && !_disposed)
                _status = Same(_status.Key, key) ? _status with { Key = key, DeviceName = state.Device.Name } : DeviceResourceStatus.Idle with { Key = key, DeviceName = state.Device.Name };
        }
        if (newKey)
        {
            Raise();
            // Cache size and date for the settings page; disk work stays off the Bluetooth thread.
            _ = Task.Run(() =>
            {
                if (ReadCache(key) is { } cache) Publish(key, s => s with { Cache = cache });
            });
        }
        lock (_gate)
        {
            if (_disposed || _shutdown.IsCancellationRequested || _pending.ContainsKey((key.CacheIdentity, key.ColorId))
                || (_nextAttempt.TryGetValue((key.CacheIdentity, key.ColorId), out var next) && next > DateTimeOffset.UtcNow)) return;
        }
        _ = ObserveAsync(RefreshAsync(key));
    }

    public Task<ResourceDownloadResult> RefreshAsync(DeviceResourceKey key, bool force = false)
    {
        if (!key.CanDownload)
            return Task.FromException<ResourceDownloadResult>(new InvalidOperationException("Product and color are not available yet."));
        var identity = (key.CacheIdentity, key.ColorId);
        lock (_gate)
        {
            if (_disposed) return Task.FromException<ResourceDownloadResult>(new ObjectDisposedException(nameof(DeviceResourceSync)));
            if (_pending.TryGetValue(identity, out var running)) return running;
            var task = Task.Run(async () =>
            {
                bool success = false;
                Publish(key, s => s with { Stage = ResourceSyncStage.Checking, Package = 0, Packages = 0, Received = 0, Total = null, Failure = ResourceSyncFailure.None });
                try
                {
                    var result = await downloader.EnsureAsync(key, force, _shutdown.Token, new Reporter(this, key)).ConfigureAwait(false);
                    success = true;
                    var cache = ReadCache(key);
                    Publish(key, s => s with
                    {
                        Stage = result.FromCache ? ResourceSyncStage.UpToDate : ResourceSyncStage.Completed,
                        Received = 0, Total = null, Cache = cache ?? s.Cache,
                    });
                    return result;
                }
                catch (Exception ex) when (!_shutdown.IsCancellationRequested)
                {
                    // An existing cache stays in use; the page shows its size and date next to the error.
                    var cache = ReadCache(key);
                    Publish(key, s => s with { Stage = ResourceSyncStage.Failed, Failure = DeviceResourceStatus.Classify(ex), Received = 0, Total = null, Cache = cache ?? s.Cache });
                    throw;
                }
                finally
                {
                    lock (_gate)
                    {
                        _pending.Remove(identity);
                        _nextAttempt[identity] = DateTimeOffset.UtcNow.Add(success ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(5));
                    }
                }
            });
            _pending[identity] = task;
            return task;
        }
    }

    private ResourceCacheInfo? ReadCache(DeviceResourceKey key)
    {
        try { return downloader.GetCacheInfo(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private static bool Same(DeviceResourceKey? a, DeviceResourceKey? b) => a is not null && b is not null && a.CacheIdentity == b.CacheIdentity && a.ColorId == b.ColorId;

    /// <summary>Applies <paramref name="change"/> to the status of <paramref name="key"/>, switching the status to that key if needed.</summary>
    private void Publish(DeviceResourceKey key, Func<DeviceResourceStatus, DeviceResourceStatus> change)
    {
        if (DeviceResourceKey.FromState(store.Current) is { CanDownload: true } active && !Same(active, key)) return;
        lock (_gate)
        {
            if (_disposed) return;
            var current = Same(_status.Key, key) ? _status : DeviceResourceStatus.Idle with { Key = key };
            _status = change(current);
        }
        Raise();
    }

    private void Raise()
    {
        try { StatusChanged?.Invoke(this, Status); }
        catch (Exception ex) { logger.LogWarning(ex, "Artwork status handler failed"); }
    }

    private async Task ObserveAsync(Task<ResourceDownloadResult> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning("Device artwork download did not complete ({Reason}); keeping the local cache", ex.GetType().Name); }
    }

    /// <summary>Forwards downloader progress, throttling byte counts so the UI thread is not flooded.</summary>
    private sealed class Reporter(DeviceResourceSync owner, DeviceResourceKey key) : IProgress<ResourceDownloadProgress>
    {
        private long _last;
        private ResourceSyncStage _stage;
        private int _package;

        public void Report(ResourceDownloadProgress value)
        {
            long now = Environment.TickCount64;
            bool milestone = value.Stage != _stage || value.Package != _package || (value.Total is { } total && value.Received >= total);
            if (!milestone && now - _last < ProgressIntervalMs) return;
            _last = now;
            _stage = value.Stage;
            _package = value.Package;
            owner.Publish(key, s => s with
            {
                Stage = value.Stage, Package = value.Package, Packages = value.Packages, Received = value.Received, Total = value.Total,
                Failure = ResourceSyncFailure.None,
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        store.StateChanged -= OnStateChanged;
        await _shutdown.CancelAsync();
        Task[] pending;
        lock (_gate) pending = _pending.Values.Cast<Task>().ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception) { /* Requests are observed by callers/background handlers. */ }
        _shutdown.Dispose();
    }
}
