// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Packaging;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// EncoDeck's side of A2DP Windows Bridge: installs the pinned component, finds the user's own copy, starts and
/// quits the core, follows it over its pipe and marks the active device as connected through the bridge (Windows
/// cannot see that link). Costs nothing while no A2DPWB exists beyond one mutex check per interval.
/// </summary>
public sealed class A2dpBridgeCompanion : IAsyncDisposable
{
    private readonly IDeviceStateStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly string? _instance;
    private BridgeInstallation? _installed;
    // Read from disk when installations may have changed, not on every core event.
    private InstalledComponent? _managed;
    private string? _runningDirectory;
    private bool _wasConnected;
    private bool _started;

    /// <param name="instance">A side-by-side A2DPWB test core (A2DPWB_INSTANCE); null for the user's own.</param>
    public A2dpBridgeCompanion(string componentsRoot, IDeviceStateStore store, ISettingsStore settings, ILogger logger, string? instance = null)
    {
        _instance = instance;
        _store = store;
        _settings = settings;
        _logger = logger;
        Component = A2dpBridgeComponent.CreateInstaller(new HttpClient(), Path.Combine(componentsRoot, "A2DPWB"));
        Monitor = new A2dpBridgeMonitor(logger, instance);
        Monitor.Changed += (_, _) => OnMonitorChanged();
        _store.StateChanged += OnStateChanged;
    }

    /// <summary>The installed EncoDeck-managed copy, as of the last <see cref="RefreshInstallations"/>.</summary>
    public InstalledComponent? Managed => _managed;

    /// <summary>The managed copy is installed but this app pins a newer one.</summary>
    public bool ManagedUpdateAvailable => _managed is not null && Component.Package is { IsPublished: true } p && _managed.Sha256 != p.Sha256;

    /// <summary>The EncoDeck-managed copy (the mirrored official build).</summary>
    public ComponentInstaller Component { get; }

    public A2dpBridgeMonitor Monitor { get; }

    public BridgeStatus Status => Monitor.Status;

    /// <summary>Raised on a background thread when the core's state, the installation or the link changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The Store build cannot download or start programs outside its package; it only connects to a running core.</summary>
    public static bool CanManage => !PackageIdentity.IsPackaged;

    /// <summary>
    /// The copy to start: a running core's folder, then A2DPWB's own installation (it updates itself), then the
    /// EncoDeck-managed copy.
    /// </summary>
    public BridgeInstallation? Installation
    {
        get
        {
            if (Status.IsConnected && _runningDirectory is { } running)
            {
                var managed = _managed;
                var origin = managed is not null && SamePath(running, managed.Directory) ? BridgeOrigin.Managed
                    : _installed is not null && SamePath(running, _installed.Directory) ? BridgeOrigin.Installed : BridgeOrigin.Running;
                return new BridgeInstallation(running, origin, A2dpBridgeComponent.FileVersion(Path.Combine(running, A2dpBridgeComponent.CoreExe)));
            }
            if (_installed is { } own && A2dpBridgeComponent.IsBridgeFolder(own.Directory)) return own;
            return _managed is { } m && A2dpBridgeComponent.IsBridgeFolder(m.Directory)
                ? new BridgeInstallation(m.Directory, BridgeOrigin.Managed, ManagedVersion(m.Version)) : null;
        }
    }

    // A device switch resets the state: mark the new device again if the bridge streams to it.
    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (e.Current.Bridge != BridgeDeviceMatch.LinkFor(e.Current.Device, Monitor.Status)) Project();
    }

    private void OnMonitorChanged()
    {
        bool connected = Monitor.Status.IsConnected;
        if (connected != _wasConnected)
        {
            _wasConnected = connected;
            // Once per session: which copy runs (process enumeration is not free).
            _runningDirectory = connected ? A2dpBridgeComponent.RunningCoreDirectory() : null;
        }
        Project();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Upstream version of a component version ("2.0.1-r1" → "2.0.1").</summary>
    private static string ManagedVersion(string version) => version.Split("-r", 2)[0];

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshInstallations();
        Monitor.Start();
        _ = Task.Run(async () =>
        {
            // Old versions and unfinished downloads; a copy whose core is running is kept.
            Component.CleanUp();
            if (_settings.Current.A2dpBridgeAutoStart && CanManage && !A2dpBridgeMonitor.IsCoreRunning(_instance) && Installation is { } installation)
            {
                try
                {
                    _logger.LogInformation("Starting A2DPWB ({Origin})", installation.Origin);
                    A2dpBridgeComponent.StartCore(installation);
                    await Monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _logger.LogWarning("Could not start A2DPWB: {Error}", ex.Message);
                }
            }
        });
    }

    /// <summary>Looks again for A2DPWB's own installation (after installing it, or when the Labs page opens).</summary>
    public void RefreshInstallations()
    {
        _installed = A2dpBridgeComponent.FindInstalled(Component.Root);
        _managed = Component.Installed;
        // A user without any A2DPWB pays for one mutex check every half minute; with one, a new core is found sooner.
        Monitor.IdleInterval = _installed is not null || _managed is not null ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task InstallAsync(IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        await Component.InstallAsync(progress, ct).ConfigureAwait(false);
        RefreshInstallations();
    }

    /// <summary>Starts the core in the background and waits until EncoDeck is connected to it.</summary>
    public async Task<bool> StartCoreAsync(CancellationToken ct = default)
    {
        if (Status.IsConnected) return true;
        if (!A2dpBridgeMonitor.IsCoreRunning(_instance))
        {
            var installation = Installation ?? throw new InvalidOperationException("A2DPWB is not installed.");
            A2dpBridgeComponent.StartCore(installation);
        }
        return await Monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>Ends the core as its tray's Exit does (the stream stops, the adapter is reset).</summary>
    public async Task QuitCoreAsync()
    {
        try { await Monitor.RequestAsync("app.quit", timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        // The core may close the pipe before it answers.
        catch (IOException) { }
        Monitor.Wake();
    }

    public Task StartStreamAsync(string profile) =>
        Monitor.RequestAsync("stream.start", new JsonObject { ["profile"] = profile }, TimeSpan.FromSeconds(60));

    public Task StopStreamAsync() => Monitor.RequestAsync("stream.stop", timeout: TimeSpan.FromSeconds(20));

    public Task SetVolumeAsync(int volume) =>
        Monitor.RequestAsync("stream.set_volume", new JsonObject { ["volume"] = Math.Clamp(volume, 0, 127) });

    /// <summary>Assigns WinUSB (the core asks for administrator rights): the adapter leaves Windows' Bluetooth.</summary>
    public Task AssignWinUsbAsync(string instanceId) =>
        Monitor.RequestAsync("adapter.assign_winusb", new JsonObject { ["instance_id"] = instanceId }, TimeSpan.FromMinutes(5));

    /// <summary>Gives the adapter back to Windows' Bluetooth driver (administrator rights).</summary>
    public Task RestoreDriverAsync(string instanceId) =>
        Monitor.RequestAsync("adapter.restore_driver", new JsonObject { ["instance_id"] = instanceId }, TimeSpan.FromMinutes(5));

    public Task RefreshAdaptersAsync() => Monitor.RequestAsync("adapter.list", timeout: TimeSpan.FromSeconds(20));

    /// <summary>Inquiry on A2DPWB's adapter (the found devices arrive as a state event too).</summary>
    public Task ScanAsync(int seconds = 10) =>
        Monitor.RequestAsync("device.scan", new JsonObject { ["seconds"] = seconds }, TimeSpan.FromSeconds(seconds + 30));

    /// <summary>
    /// Saves a connection profile for <paramref name="address"/> with A2DPWB's own defaults for a new profile
    /// (system audio, volume sync, codec fallback); the first connection pairs. Returns the profile's name, which
    /// never replaces an existing profile.
    /// </summary>
    public async Task<string> SaveProfileAsync(string deviceName, string address, string codec)
    {
        var snapshot = Status.Snapshot ?? throw new BridgeUnavailableException();
        string baseName = deviceName.Trim().Length > 0 ? deviceName.Trim() : address;
        string name = baseName;
        for (int n = 2; snapshot.Profiles.Any(p => p.Name == name); n++) name = $"{baseName} {n}";
        var profile = new JsonObject
        {
            ["name"] = name, ["device_address"] = address, ["device_name"] = deviceName.Trim(),
            ["codec"] = codec, ["quality"] = "hq", ["abr"] = false, ["codec_fallback"] = true,
            ["sample_rate"] = 0, ["bit_depth"] = 0, ["capture_mode"] = "loopback",
            ["audio_device_id"] = "", ["audio_device_name"] = "", ["auto_switch_device"] = true,
            ["app_exe"] = "", ["app_name"] = "", ["volume_sync"] = "auto", ["volume_device_id"] = "", ["volume_device_name"] = "",
            ["max_media_payload"] = 1023, ["le_audio"] = false, ["le_low_latency"] = true,
        };
        await Monitor.RequestAsync("profile.save", new JsonObject { ["profile"] = profile, ["original_name"] = null }).ConfigureAwait(false);
        return name;
    }

    /// <summary>A2DPWB's diagnostic text with Bluetooth addresses masked, for EncoDeck's diagnostics package.</summary>
    public async Task<string?> DiagnosticsAsync()
    {
        if (!Status.IsConnected) return null;
        try
        {
            var result = await Monitor.RequestAsync("diag.info", new JsonObject { ["mask"] = true }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return result?["text"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { return null; }
    }

    /// <summary>Marks the active device as connected through the bridge while the bridge streams to its address.</summary>
    private void Project()
    {
        var status = Monitor.Status;
        _store.TryUpdate(null, s =>
        {
            var link = BridgeDeviceMatch.LinkFor(s.Device, status);
            return s.Bridge == link ? s : s with { Bridge = link };
        });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        _store.StateChanged -= OnStateChanged;
        await Monitor.DisposeAsync().ConfigureAwait(false);
    }
}
