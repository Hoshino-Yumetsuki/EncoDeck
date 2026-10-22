// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Bluetooth;

/// <summary>A paired device EncoDeck has a profile for.</summary>
public sealed record KnownDevice(DeviceIdentity Identity, IDeviceProfile Profile, bool IsConnected, bool IsActive);

public interface IDeviceManager : IDeviceCommandService, IAdvancedDeviceCommands, IAsyncDisposable
{
    IReadOnlyList<KnownDevice> KnownDevices { get; }

    /// <summary>Raised when the list of known devices or their link state changes.</summary>
    event EventHandler? KnownDevicesChanged;

    /// <summary>Raised when the active device's Bluetooth link comes up (drives the connection popup).</summary>
    event EventHandler<DeviceIdentity>? ActiveDeviceLinkConnected;

    /// <summary>Raised when the active device's case lid opens while connected.</summary>
    event EventHandler<DeviceIdentity>? ActiveDeviceCaseLidOpened;

    void Start();

    Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Call after the system resumes from sleep: re-validates the control channel.</summary>
    void NotifySystemResumed();
}

/// <summary>
/// Composition root of the Bluetooth runtime: watches paired devices, picks the active supported
/// device, runs its <see cref="ControlChannelSupervisor"/>, and routes commands to it.
/// </summary>
public sealed class DeviceManager : IDeviceManager
{
    private readonly PairedDeviceWatcher _watcher;
    private readonly IDeviceProfileRegistry _profiles;
    private readonly IDeviceStateStore _store;
    private readonly IChannelConnector _connector;
    private readonly Func<string?> _preferredDeviceId;
    private readonly Func<bool> _allowExperimental;
    private readonly ReconnectPolicy _policy;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly IProtocolTrace? _trace;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ActiveDevice? _active;
    private string? _manualSelection;

    public DeviceManager(
        IDeviceProfileRegistry profiles,
        IDeviceStateStore store,
        IChannelConnector connector,
        Func<string?> preferredDeviceId,
        Func<bool> allowExperimental,
        ILoggerFactory? loggers = null,
        ReconnectPolicy? policy = null,
        IProtocolTrace? trace = null)
    {
        _profiles = profiles;
        _store = store;
        _connector = connector;
        _preferredDeviceId = preferredDeviceId;
        _allowExperimental = allowExperimental;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<DeviceManager>();
        _policy = policy ?? ReconnectPolicy.Default;
        _trace = trace;
        _watcher = new PairedDeviceWatcher(_loggers.CreateLogger<PairedDeviceWatcher>());
        _watcher.Changed += OnPairedDevicesChanged;
    }

    public IReadOnlyList<KnownDevice> KnownDevices
    {
        get
        {
            string? activeId = _active?.Identity.DeviceId;
            return _watcher.Devices
                .Select(d => (Device: d, Profile: _profiles.Match(d.Identity)))
                .Where(x => x.Profile is not null)
                // The read-only generic profile matches by name ("OPPO …"), which also hits phones and
                // watches; only accept it for audio devices. Exact model profiles are trusted as-is.
                .Where(x => !IsGeneric(x.Profile!) || x.Device.IsAudioDevice)
                .Select(x => new KnownDevice(x.Device.Identity, x.Profile!, x.Device.IsConnected, x.Device.Identity.DeviceId == activeId))
                .ToList();
        }
    }

    public event EventHandler? KnownDevicesChanged;

    public event EventHandler<DeviceIdentity>? ActiveDeviceLinkConnected;

    public event EventHandler<DeviceIdentity>? ActiveDeviceCaseLidOpened;

    public void Start() => _watcher.Start();

    public async Task SelectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        _manualSelection = deviceId;
        await ReselectAsync(cancellationToken).ConfigureAwait(false);
    }

    public void NotifySystemResumed()
    {
        // Sockets opened before sleep are often dead without an error; force a clean reconnect.
        _active?.Supervisor.RequestReconnect();
    }

    private void OnPairedDevicesChanged(object? sender, EventArgs e)
    {
        KnownDevicesChanged?.Invoke(this, EventArgs.Empty);
        if (!_watcher.IsEnumerated) return;
        _ = ReselectAsync(_lifetime.Token);
    }

    private async Task ReselectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var candidates = KnownDevices;
            string? preferred = _manualSelection ?? _preferredDeviceId();
            var choice = candidates.FirstOrDefault(c => c.Identity.DeviceId == preferred)
                ?? candidates.Where(c => c.IsConnected).OrderByDescending(c => c.Profile.MatchScore(c.Identity)).FirstOrDefault()
                ?? candidates.FirstOrDefault(c => c.Identity.DeviceId == _active?.Identity.DeviceId)
                ?? candidates.OrderByDescending(c => c.Profile.MatchScore(c.Identity)).FirstOrDefault();

            if (choice?.Identity.DeviceId == _active?.Identity.DeviceId) return;

            if (_active is { } old)
            {
                _active = null;
                await old.DisposeAsync().ConfigureAwait(false);
            }

            if (choice is null)
            {
                _store.Reset(DeviceState.Empty);
                return;
            }

            _logger.LogInformation("Active device: {Device} ({Model})", choice.Identity, choice.Profile.ModelId);
            _store.Reset(DeviceState.Empty with
            {
                Device = choice.Identity,
                Profile = choice.Profile,
                Connection = choice.IsConnected ? DeviceConnectionState.AudioConnected : DeviceConnectionState.PairedNotConnected,
            });
            // The device cache has now restored what these earbuds reported last time: show the
            // same model and settings as then, before the control channel even opens.
            var profile = choice.Profile;
            if (profile is IControllableProfile matched)
            {
                var remembered = matched.Remember(_store.Current);
                if (!ReferenceEquals(remembered, matched))
                {
                    profile = remembered;
                    _store.TryUpdate(choice.Identity.DeviceId, s => s with { Profile = remembered });
                }
            }

            var link = await BluetoothLinkMonitor.CreateAsync(choice.Identity.DeviceId, cancellationToken).ConfigureAwait(false);
            if (link is null)
            {
                _logger.LogWarning("Could not open {Device} for link monitoring", choice.Identity);
                _store.TryUpdate(choice.Identity.DeviceId, s => s with { Connection = DeviceConnectionState.ControlUnavailable, LastError = new DeviceError("Windows could not open the Bluetooth device.", CommandStatus.TransportError, DateTimeOffset.UtcNow) });
                return;
            }

            if (profile is not IControllableProfile controllable)
            {
                link.Dispose();
                return;
            }

            var supervisor = new ControlChannelSupervisor(choice.Identity, controllable, link, _connector, _store, _policy, _allowExperimental, _loggers, trace: _trace);
            var identity = choice.Identity;
            supervisor.LinkConnected += (_, _) => ActiveDeviceLinkConnected?.Invoke(this, identity);
            supervisor.CaseLidOpened += (_, _) => ActiveDeviceCaseLidOpened?.Invoke(this, identity);
            _active = new ActiveDevice(identity, link, supervisor);
            supervisor.Start();
            KnownDevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device selection failed");
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private static bool IsGeneric(IDeviceProfile profile) => profile is IControllableProfile { IsGeneric: true };

    private IDeviceController? Controller => _active?.Supervisor.Controller;

    public Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken = default) =>
        Controller is { } c ? c.SetNoiseControlAsync(target, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken = default) =>
        Controller is { } c ? c.SetEqualizerPresetAsync(presetId, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken = default) =>
        Controller is { } c ? c.SetToggleAsync(toggle, enabled, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        Controller is { } c ? c.RefreshAsync(cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    private Task<CommandResult> WithController(Func<IDeviceController, Task<CommandResult>> action) =>
        Controller is { } c ? action(c) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => WithController(c => c.SetSpatialAudioAsync(mode, cancellationToken));

    public Task<CommandResult> SetGameSoundAsync(int type, CancellationToken cancellationToken = default) => WithController(c => c.SetGameSoundAsync(type, cancellationToken));

    public Task<CommandResult> RefreshGameModeAsync(CancellationToken cancellationToken = default) => WithController(c => c.RefreshGameModeAsync(cancellationToken));

    public Task<CommandResult> SetPromptVolumeAsync(int level, CancellationToken cancellationToken = default) => WithController(c => c.SetPromptVolumeAsync(level, cancellationToken));

    public Task<CommandResult> SetBassEngineLevelAsync(int level, CancellationToken cancellationToken = default) => WithController(c => c.SetBassEngineLevelAsync(level, cancellationToken));

    public Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default) => WithController(c => c.SetKeyFunctionAsync(assignment, cancellationToken));

    public Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint modeMask, CancellationToken cancellationToken = default) => WithController(c => c.SetNoiseCycleAsync(side, modeMask, cancellationToken));

    public Task<CommandResult> SetHeadMotionTypeAsync(int type, CancellationToken cancellationToken = default) => WithController(c => c.SetHeadMotionTypeAsync(type, cancellationToken));

    public Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default) => WithController(c => c.FindEarbudsAsync(ring, cancellationToken));

    public Task<CommandResult> RunFitTestAsync(CancellationToken cancellationToken = default) => WithController(c => c.RunFitTestAsync(cancellationToken));

    public Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default) => WithController(c => c.MultiConnectAsync(operation, address, cancellationToken));

    public Task<CommandResult> SetPreferredDeviceAsync(byte[]? address, CancellationToken cancellationToken = default) => WithController(c => c.SetPreferredDeviceAsync(address, cancellationToken));

    public Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => WithController(c => c.SelectCustomEqualizerAsync(id, cancellationToken));

    public Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default) => WithController(c => c.SaveCustomEqualizerAsync(preset, cancellationToken));

    public Task<CommandResult> AddCustomEqualizerAsync(CancellationToken cancellationToken = default) => WithController(c => c.AddCustomEqualizerAsync(cancellationToken));

    public Task<CommandResult> DeleteCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) => WithController(c => c.DeleteCustomEqualizerAsync(id, cancellationToken));

    public Task<CommandResult> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_active is null) return Task.FromResult(CommandResult.NotConnected);
        _active.Supervisor.RequestReconnect();
        return Task.FromResult(CommandResult.Ok);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _watcher.Changed -= OnPairedDevicesChanged;
        _watcher.Dispose();
        await _selectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active is { } active)
            {
                _active = null;
                await active.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private sealed record ActiveDevice(DeviceIdentity Identity, BluetoothLinkMonitor Link, ControlChannelSupervisor Supervisor) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync().ConfigureAwait(false);
            Link.Dispose();
        }
    }
}
