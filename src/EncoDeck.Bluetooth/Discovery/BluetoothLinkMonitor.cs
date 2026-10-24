// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.LeAudio;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Event-driven link state of one paired device via <see cref="BluetoothDevice.ConnectionStatusChanged"/>.
/// Earbuds switched to LE Audio keep only the link of their bonded LE identity at the same public
/// address, so that link counts as well. Holds both devices for its lifetime and releases them on dispose.
/// </summary>
public sealed class BluetoothLinkMonitor : ILinkStateSource
{
    private BluetoothDevice? _device;
    private List<BluetoothLEDevice>? _leDevices;
    private volatile bool _connected;

    private BluetoothLinkMonitor(BluetoothDevice device, List<BluetoothLEDevice> leDevices)
    {
        _device = device;
        _leDevices = leDevices;
        _connected = Evaluate();
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        foreach (var le in leDevices) le.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public bool IsConnected => _connected;

    public event EventHandler<bool>? ConnectionChanged;

    public static async Task<BluetoothLinkMonitor?> CreateAsync(string deviceId, CancellationToken cancellationToken)
    {
        BluetoothDevice? device;
        try
        {
            device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (device is null) return null;
        return new BluetoothLinkMonitor(device, await BondedLeIdentitiesAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The bonded LE identity at the public address plus the other bonded earbud of the pair (either
    /// may be the only one out of the case). An unpaired GATT link (e.g. GAIA control) is not audio.
    /// </summary>
    private static async Task<List<BluetoothLEDevice>> BondedLeIdentitiesAsync(ulong address, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? le;
        try
        {
            le = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
        if (le is null) return [];
        if (!le.DeviceInformation.Pairing.IsPaired)
        {
            le.Dispose();
            return [];
        }
        try
        {
            return [le, .. await LeAudioPairing.FindSecondaryBondsAsync(address, le.Name, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [le];
        }
    }

    private bool Evaluate() =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected
        || _leDevices?.Any(le => le.ConnectionStatus == BluetoothConnectionStatus.Connected) == true;

    private void OnConnectionStatusChanged(object sender, object args)
    {
        bool connected;
        try { connected = Evaluate(); }
        catch (ObjectDisposedException) { return; }
        if (connected == _connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(this, connected);
    }

    public void Dispose()
    {
        foreach (var le in Interlocked.Exchange(ref _leDevices, null) ?? [])
        {
            le.ConnectionStatusChanged -= OnConnectionStatusChanged;
            le.Dispose();
        }
        var device = Interlocked.Exchange(ref _device, null);
        if (device is null) return;
        device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        device.Dispose();
    }
}
