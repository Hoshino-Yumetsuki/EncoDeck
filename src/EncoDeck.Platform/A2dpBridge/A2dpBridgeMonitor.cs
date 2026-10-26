// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Principal;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Platform.A2dpBridge;

public enum BridgeLink
{
    /// <summary>No A2DPWB core runs for this user.</summary>
    NotRunning,

    /// <summary>The core runs; the pipe session is being opened.</summary>
    Connecting,

    Connected,

    /// <summary>The core refused EncoDeck's protocol version.</summary>
    Incompatible,
}

/// <param name="Server">"a2dpwb-core/2.0.1" while connected.</param>
/// <param name="Detail">Why the core is not usable (Incompatible), in English from the core.</param>
public sealed record BridgeStatus(BridgeLink Link, BridgeSnapshot? Snapshot = null, string? Server = null, string? Detail = null)
{
    public static BridgeStatus NotRunning { get; } = new(BridgeLink.NotRunning);

    public bool IsConnected => Link == BridgeLink.Connected && Snapshot is not null;

    /// <summary>"2.0.1" from <see cref="Server"/>.</summary>
    public string? ServerVersion => Server is { } s && s.IndexOf('/') is var slash and >= 0 ? s[(slash + 1)..] : null;
}

/// <summary>The core is not running or not connected.</summary>
public sealed class BridgeUnavailableException() : InvalidOperationException("A2DPWB is not running.");

/// <summary>
/// Follows the A2DPWB core of this user and keeps one pipe session to it while it runs. Detection is a check of
/// the core's single-instance mutex (opened and closed at once, like A2DPWB's own <c>--quit</c>), every
/// <see cref="IdleInterval"/> while no core runs; once connected, everything is pushed by the core.
/// </summary>
public sealed class A2dpBridgeMonitor : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string? _instance;
    private readonly string _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private TaskCompletionSource _wake = NewWake();
    private A2dpBridgeConnection? _connection;
    private Task _loop = Task.CompletedTask;
    private BridgeStatus _status = BridgeStatus.NotRunning;

    /// <param name="instance">A2DPWB's <c>A2DPWB_INSTANCE</c> (a side-by-side test core); null for the normal one.</param>
    public A2dpBridgeMonitor(ILogger? logger = null, string? instance = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _instance = instance;
        _client = $"encodeck/{SemanticVersion.FromAssembly(typeof(A2dpBridgeMonitor).Assembly)}";
    }

    /// <summary>How often to look for a core while none runs.</summary>
    public TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(10);

    public BridgeStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    public static string PipeName(string? instance = null) => $"a2dpwb-{WindowsIdentity.GetCurrent().User?.Value}{Suffix(instance)}";

    public static string MutexName(string? instance = null) => $@"Local\A2DPWB_v2_Core{Suffix(instance)}";

    private static string Suffix(string? instance) => string.IsNullOrEmpty(instance) ? "" : "-" + instance;

    /// <summary>
    /// Whether a core runs in this session. The handle is closed at once: holding it would keep the mutex alive
    /// after the core exits and make the next core believe another one runs.
    /// </summary>
    public static bool IsCoreRunning(string? instance = null)
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName(instance), out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return false; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (!_loop.IsCompleted) return;
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Looks for the core now instead of at the next interval (after starting it, or when its page opens).</summary>
    public void Wake()
    {
        TaskCompletionSource wake;
        lock (_gate) { wake = _wake; _wake = NewWake(); }
        wake.TrySetResult();
    }

    /// <summary>Waits until a session is open (true) or the timeout passes.</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        deadline.CancelAfter(timeout);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, EventArgs e) { if (Status.IsConnected) connected.TrySetResult(); }
        Changed += OnChanged;
        try
        {
            if (Status.IsConnected) return true;
            Wake();
            await connected.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Status.IsConnected; }
        finally { Changed -= OnChanged; }
    }

    /// <summary>A request to the connected core; <see cref="BridgeUnavailableException"/> when there is none.</summary>
    public Task<JsonNode?> RequestAsync(string method, JsonNode? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        A2dpBridgeConnection? connection;
        lock (_gate) connection = _connection;
        return connection is null
            ? Task.FromException<JsonNode?>(new BridgeUnavailableException())
            : connection.RequestAsync(method, parameters, timeout ?? TimeSpan.FromSeconds(15), ct);
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var stopped = Task.Delay(Timeout.Infinite, token);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsCoreRunning(_instance))
                {
                    SetStatus(BridgeStatus.NotRunning);
                    await IdleAsync(IdleInterval, token).ConfigureAwait(false);
                    continue;
                }
                SetStatus(new BridgeStatus(BridgeLink.Connecting));
                var connection = await ConnectAsync(token).ConfigureAwait(false);
                if (connection is null)
                {
                    // Running but no pipe yet (just started, or still starting up): look again soon.
                    SetStatus(BridgeStatus.NotRunning);
                    await IdleAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    continue;
                }
                await using (connection)
                {
                    lock (_gate) _connection = connection;
                    _logger.LogInformation("A2DPWB connected ({Server})", connection.Server);
                    connection.Changed += snapshot => SetStatus(new BridgeStatus(BridgeLink.Connected, snapshot, connection.Server));
                    SetStatus(new BridgeStatus(BridgeLink.Connected, connection.Snapshot, connection.Server));
                    await Task.WhenAny(connection.Completion, stopped).ConfigureAwait(false);
                    lock (_gate) _connection = null;
                }
                _logger.LogInformation("A2DPWB disconnected");
                SetStatus(BridgeStatus.NotRunning);
                // The core is exiting or restarting: give it a moment before looking again.
                await IdleAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (BridgeRejectedException ex)
            {
                _logger.LogWarning("A2DPWB refused the connection: {Reason}", ex.Message);
                SetStatus(new BridgeStatus(BridgeLink.Incompatible, Detail: ex.Message));
                await IdleAsync(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("A2DPWB session failed: {Error}", ex.Message);
                SetStatus(BridgeStatus.NotRunning);
                try { await IdleAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<A2dpBridgeConnection?> ConnectAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await A2dpBridgeConnection.OpenAsync(PipeName(_instance), _client, TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or IOException and not BridgeRejectedException)
            {
                if (!IsCoreRunning(_instance)) return null;
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task IdleAsync(TimeSpan delay, CancellationToken token)
    {
        Task wake;
        lock (_gate) wake = _wake.Task;
        await Task.WhenAny(wake, Task.Delay(delay, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private void SetStatus(BridgeStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "A2DPWB status handler failed"); }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Wake();
        try { await _loop.ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
    }
}
