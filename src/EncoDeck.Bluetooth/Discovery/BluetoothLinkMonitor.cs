// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Control;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Event-driven link state of one paired device via <see cref="BluetoothDevice.ConnectionStatusChanged"/>.
/// Holds the <see cref="BluetoothDevice"/> for its lifetime and releases it on dispose.
/// </summary>
public sealed class BluetoothLinkMonitor : ILinkStateSource
{
    private BluetoothDevice? _device;
    private volatile bool _connected;

    private BluetoothLinkMonitor(BluetoothDevice device)
    {
        _device = device;
        _connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public bool IsConnected => _connected;

    public event EventHandler<bool>? ConnectionChanged;

    public static async Task<BluetoothLinkMonitor?> CreateAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken);
            return device is null ? null : new BluetoothLinkMonitor(device);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void OnConnectionStatusChanged(BluetoothDevice sender, object args)
    {
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        if (connected == _connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(this, connected);
    }

    public void Dispose()
    {
        var device = Interlocked.Exchange(ref _device, null);
        if (device is null) return;
        device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        device.Dispose();
    }
}
