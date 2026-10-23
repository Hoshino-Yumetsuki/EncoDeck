// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Bluetooth.Control;

/// <summary>
/// Owns the control channel lifecycle for one device:
/// <code>
///   link down ──► PairedNotConnected / Disconnected (wait for link)
///   link up   ──► ControlConnecting ──ok──► ControlReady ──drop──► Reconnecting (backoff) ─┐
///                        │                                                                 │
///                        └─fail─► Reconnecting (bounded exponential backoff) ◄────────────┘
///                                     └─retries exhausted / service missing─► ControlUnavailable
///                                          (until the user retries or the link reconnects)
/// </code>
/// There is never more than one connection attempt in flight and never an unbounded retry loop.
/// </summary>
public sealed class ControlChannelSupervisor : IAsyncDisposable
{
    /// <summary>Settle time after Windows reports the link up before opening RFCOMM.</summary>
    public static readonly TimeSpan DefaultLinkSettleDelay = TimeSpan.FromSeconds(1);

    private readonly DeviceIdentity _identity;
    private readonly IControllableProfile _profile;
    private readonly ILinkStateSource _link;
    private readonly IChannelConnector _connector;
    private readonly IDeviceStateStore _store;
    private readonly ReconnectPolicy _policy;
    private readonly Func<bool> _allowExperimental;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IProtocolTrace? _trace;
    private readonly TimeSpan _linkSettleDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource _wake = NewWake();
    private bool _manualRetry;
    private Task? _loop;
    private IDeviceController? _controller;

    public ControlChannelSupervisor(
        DeviceIdentity identity,
        IControllableProfile profile,
        ILinkStateSource link,
        IChannelConnector connector,
        IDeviceStateStore store,
        ReconnectPolicy? policy = null,
        Func<bool>? allowExperimental = null,
        ILoggerFactory? loggers = null,
        TimeProvider? time = null,
        IProtocolTrace? trace = null,
        TimeSpan? linkSettleDelay = null)
    {
        _identity = identity;
        _profile = profile;
        _link = link;
        _connector = connector;
        _store = store;
        _policy = policy ?? ReconnectPolicy.Default;
        _allowExperimental = allowExperimental ?? (() => false);
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<ControlChannelSupervisor>();
        _time = time ?? TimeProvider.System;
        _trace = trace;
        _linkSettleDelay = linkSettleDelay ?? DefaultLinkSettleDelay;
        _link.ConnectionChanged += OnLinkChanged;
    }

    /// <summary>The controller while the channel is ready; null otherwise.</summary>
    public IDeviceController? Controller
    {
        get { lock (_gate) return _controller; }
    }

    /// <summary>Raised when the link goes from down to up (not for the initial state).</summary>
    public event EventHandler? LinkConnected;

    /// <summary>Raised when the case lid opens while connected (official pop-up trigger).</summary>
    public event EventHandler? CaseLidOpened;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    /// <summary>User-initiated retry: resets the backoff and reconnects now.</summary>
    public void RequestReconnect()
    {
        lock (_gate) _manualRetry = true;
        Wake();
    }

    private void OnLinkChanged(object? sender, bool connected)
    {
        _logger.LogInformation("Bluetooth link {State}", connected ? "up" : "down");
        if (connected) LinkConnected?.Invoke(this, EventArgs.Empty);
        Wake();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        bool exhausted = false;
        bool everLinked = false;
        bool linkWasUp = false;
        bool firstIteration = true;

        while (!ct.IsCancellationRequested)
        {
            var wake = CurrentWake();

            if (!_link.IsConnected)
            {
                attempt = 0;
                exhausted = false;
                linkWasUp = false;
                firstIteration = false;
                SetConnection(everLinked ? DeviceConnectionState.Disconnected : DeviceConnectionState.PairedNotConnected);
                await WaitAsync(wake, null, ct).ConfigureAwait(false);
                continue;
            }

            if (!linkWasUp)
            {
                // Fresh link: forget earlier failures. Unless the link was already up when we started,
                // give Windows a moment to finish profile setup before opening RFCOMM.
                linkWasUp = true;
                everLinked = true;
                attempt = 0;
                exhausted = false;
                SetConnection(DeviceConnectionState.AudioConnected);
                if (!firstIteration)
                {
                    await WaitAsync(wake, _linkSettleDelay, ct).ConfigureAwait(false);
                    continue;
                }
            }
            firstIteration = false;

            if (TakeManualRetry())
            {
                attempt = 0;
                exhausted = false;
            }

            if (exhausted)
            {
                SetConnection(DeviceConnectionState.ControlUnavailable);
                await WaitAsync(wake, null, ct).ConfigureAwait(false);
                continue;
            }

            SetConnection(DeviceConnectionState.ControlConnecting);
            var outcome = await ConnectAndServeAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            switch (outcome)
            {
                case ServeOutcome.EndedByLinkOrUser:
                    attempt = 0;
                    continue;
                case ServeOutcome.Permanent:
                    exhausted = true;
                    continue;
            }

            attempt++;
            if (_policy.GetDelay(attempt) is not { } delay)
            {
                _logger.LogWarning("Control channel retries exhausted after {Attempts} attempts", attempt - 1);
                exhausted = true;
                continue;
            }
            SetConnection(DeviceConnectionState.Reconnecting);
            await WaitAsync(CurrentWake(), delay, ct).ConfigureAwait(false);
        }
    }

    private enum ServeOutcome
    {
        /// <summary>Retry with backoff.</summary>
        Failed,
        /// <summary>Do not retry automatically (e.g. no control service on this device).</summary>
        Permanent,
        /// <summary>Session ended because the link dropped or the user asked to reconnect.</summary>
        EndedByLinkOrUser,
    }

    private async Task<ServeOutcome> ConnectAndServeAsync(CancellationToken ct)
    {
        ChannelOpenResult open;
        try
        {
            open = await _connector.OpenAsync(_identity.DeviceId, _profile.ControlServiceUuids, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ServeOutcome.EndedByLinkOrUser;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening the control channel threw");
            SetError("Could not open the control channel.");
            return ServeOutcome.Failed;
        }

        if (open.Status != ChannelOpenStatus.Opened || open.Channel is null)
        {
            _logger.LogInformation("Control channel not opened: {Status} {Detail}", open.Status, open.Detail);
            SetError(open.Status switch
            {
                ChannelOpenStatus.ServiceNotFound => "The earbuds do not expose a supported control service.",
                ChannelOpenStatus.AccessDenied => "Windows denied access to the Bluetooth device.",
                ChannelOpenStatus.ControlEndpointNotFound => open.Detail ?? "The Bluetooth LE control endpoint was not found.",
                _ => "Could not open the control channel.",
            });
            return open.Status is ChannelOpenStatus.ServiceNotFound or ChannelOpenStatus.AccessDenied or ChannelOpenStatus.ControlEndpointNotFound ? ServeOutcome.Permanent : ServeOutcome.Failed;
        }

        // Reconnecting: start from the model and settings the earbuds had (the UI keeps showing them).
        var profile = _store.Current is { Profile: IControllableProfile current } state && state.Device?.DeviceId == _identity.DeviceId && current.Brand == _profile.Brand ? current : _profile;
        IDeviceController? controller = null;
        try
        {
            controller = profile.CreateController(open.Channel, new ControllerContext(_identity, _store, _allowExperimental, _loggers, _time, _trace));
            var closed = controller.Closed;
            controller.CaseLidOpened += (_, _) => CaseLidOpened?.Invoke(this, EventArgs.Empty);
            bool initialized = await controller.InitializeAsync(ct).ConfigureAwait(false);

            if (!initialized)
            {
                SetError("The earbuds did not answer the control handshake.");
                return ServeOutcome.Failed;
            }

            if (!_link.IsConnected || ct.IsCancellationRequested) return ServeOutcome.EndedByLinkOrUser;
            if (closed.IsCompleted)
            {
                SetError("The control channel closed during the handshake.");
                return ServeOutcome.Failed;
            }

            lock (_gate) _controller = controller;
            SetConnection(DeviceConnectionState.ControlReady);
            _store.TryUpdate(_identity.DeviceId, s => s with { LastError = null });

            while (true)
            {
                var wake = CurrentWake();
                await Task.WhenAny(closed, wake.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return ServeOutcome.EndedByLinkOrUser;
                if (closed.IsCompleted)
                {
                    _logger.LogInformation("Control channel closed while the link is {Link}", _link.IsConnected ? "up" : "down");
                    return _link.IsConnected ? ServeOutcome.Failed : ServeOutcome.EndedByLinkOrUser;
                }
                if (!_link.IsConnected) return ServeOutcome.EndedByLinkOrUser;
                if (TakeManualRetry()) return ServeOutcome.EndedByLinkOrUser;
                // Spurious wake (e.g. duplicate link-up event): keep serving.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ServeOutcome.EndedByLinkOrUser;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initializing or serving the control channel failed");
            SetError("The control handshake failed.");
            return ServeOutcome.Failed;
        }
        finally
        {
            lock (_gate) _controller = null;
            if (controller is not null) await controller.DisposeAsync().ConfigureAwait(false);
            else await open.Channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool TakeManualRetry()
    {
        lock (_gate)
        {
            bool value = _manualRetry;
            _manualRetry = false;
            return value;
        }
    }

    private void SetConnection(DeviceConnectionState state)
    {
        _store.TryUpdate(_identity.DeviceId, s => s.Connection == state ? s : s with { Connection = state });
    }

    private void SetError(string message)
    {
        _store.TryUpdate(_identity.DeviceId, s => s with { LastError = new DeviceError(message, CommandStatus.TransportError, _time.GetUtcNow()) });
    }

    private TaskCompletionSource CurrentWake()
    {
        lock (_gate) return _wake;
    }

    private void Wake()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _wake;
            _wake = NewWake();
        }
        previous.TrySetResult();
    }

    private async Task WaitAsync(TaskCompletionSource wake, TimeSpan? delay, CancellationToken ct)
    {
        var tasks = new List<Task> { wake.Task, Task.Delay(Timeout.Infinite, ct) };
        if (delay is { } d) tasks.Add(Task.Delay(d, _time, ct));
        await Task.WhenAny(tasks).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        _link.ConnectionChanged -= OnLinkChanged;
        _lifetime.Cancel();
        Wake();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Supervisor loop did not stop within 5 s");
            }
        }
    }
}
