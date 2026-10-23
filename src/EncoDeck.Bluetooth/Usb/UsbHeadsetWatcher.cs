// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Usb;

/// <summary>Watches audio-render endpoints and confirms USB ancestry in the same device container.</summary>
public sealed class UsbHeadsetWatcher : IDisposable
{
    internal const string ContainerKey = "System.Devices.ContainerId";
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _endpoints = [];
    private readonly Dictionary<string, PairedDevice> _devices = [];
    private readonly Dictionary<string, Guid> _containers = [];
    private readonly Dictionary<string, long> _revisions = [];
    private long _revision;
    private readonly CancellationTokenSource _stop = new();
    private DeviceWatcher? _watcher;
    public IReadOnlyList<PairedDevice> Devices { get { lock (_gate) return _devices.Values.ToList(); } }
    public bool IsEnumerated { get; private set; }
    public event EventHandler? Changed;
    public Guid? Container(string id) { lock (_gate) return _containers.TryGetValue(id, out var value) ? value : null; }
    public void Start()
    {
        if (_watcher is not null) return;
        // Audio render interface class; input-only microphones and arbitrary HID devices are excluded.
        _watcher = DeviceInformation.CreateWatcher("System.Devices.InterfaceClassGuid:=\"{e6327cad-dcec-4949-ae8a-991e976a79d2}\"",
            [ContainerKey], DeviceInformationKind.DeviceInterface);
        _watcher.Added += Added;
        _watcher.Updated += Updated;
        _watcher.Removed += Removed;
        _watcher.EnumerationCompleted += Completed;
        _watcher.Start();
    }
    private void Added(DeviceWatcher sender, DeviceInformation info)
    {
        long revision;
        lock (_gate) { _endpoints[info.Id] = info; revision = _revisions[info.Id] = ++_revision; }
        _ = VerifyAsync(info, revision);
    }
    private void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        DeviceInformation? info;
        long revision;
        lock (_gate) { _endpoints.TryGetValue(update.Id, out info); info?.Update(update); revision = _revisions[update.Id] = ++_revision; }
        if (info is not null) _ = VerifyAsync(info, revision);
    }
    private void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate) { _endpoints.Remove(update.Id); _devices.Remove(update.Id); _containers.Remove(update.Id); _revisions.Remove(update.Id); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Completed(DeviceWatcher sender, object args) { IsEnumerated = true; Changed?.Invoke(this, EventArgs.Empty); }
    private async Task VerifyAsync(DeviceInformation info, long revision)
    {
        try
        {
            var identity = new DeviceIdentity(info.Id, info.Name, new BluetoothAddress(0)) { Transport = DeviceTransport.Usb };
            bool named = UsbHeadsetProfiles.Create().Any(p => p.MatchScore(identity) > 0);
            bool valid = false;
            Guid container = default;
            if (info.IsEnabled && info.Properties.TryGetValue(ContainerKey, out var value) && value is Guid id && id != Guid.Empty)
            {
                container = id;
                var members = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                    ["System.Devices.HardwareIds"], DeviceInformationKind.Device).AsTask(_stop.Token).ConfigureAwait(false);
                string[] hardware = members.SelectMany(m => m.Properties.TryGetValue("System.Devices.HardwareIds", out var ids) && ids is string[] list ? list : []).ToArray();
                if (UsbHeadsetProfiles.MatchHardware(hardware) is { } exact)
                { identity = identity with { Name = exact.DisplayName }; named = true; }
                valid = named && hardware.Any(h => h.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase));
            }
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || !_revisions.TryGetValue(info.Id, out var current) || current != revision) return;
                if (valid) { _devices[info.Id] = new(identity, true); _containers[info.Id] = container; }
                else { _devices.Remove(info.Id); _containers.Remove(info.Id); }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // USB unplug/access denial races are normal; they must not stop Bluetooth discovery.
        }
    }
    public void Dispose()
    {
        _stop.Cancel();
        if (_watcher is { } watcher)
        {
            watcher.Added -= Added; watcher.Updated -= Updated; watcher.Removed -= Removed; watcher.EnumerationCompleted -= Completed;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }
}
