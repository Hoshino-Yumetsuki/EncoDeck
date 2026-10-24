// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.LeAudio;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Event-driven link state of one paired device via <see cref="BluetoothDevice.ConnectionStatusChanged"/>.
/// Earbuds switched to LE Audio keep only the links of their bonded LE identities (the one at the same
/// public address and the other earbud), so those count as well. Holds the devices for its lifetime
/// and releases them on dispose.
/// </summary>
public sealed class BluetoothLinkMonitor : ILinkStateSource
{
    private static readonly TimeSpan SecondaryRetry = TimeSpan.FromSeconds(15);
    private const int SecondaryAttempts = 8;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BluetoothDevice? _device;
    private ImmutableArray<BluetoothLEDevice> _leDevices;
    private volatile bool _connected;
    private volatile bool _leAudio;
    private int _searching;
    private bool _disposed;

    private BluetoothLinkMonitor(BluetoothDevice device, ImmutableArray<BluetoothLEDevice> leDevices)
    {
        _device = device;
        _leDevices = leDevices;
        _connected = Evaluate();
        _leAudio = EvaluateLe();
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        foreach (var le in leDevices) le.ConnectionStatusChanged += OnConnectionStatusChanged;
        // Only the primary bonded so far (e.g. the LE Audio switch is still pairing the other earbud).
        if (leDevices.Length == 1) _ = FindSecondaryAsync(SecondaryAttempts);
    }

    public bool IsConnected => _connected;

    public event EventHandler<bool>? ConnectionChanged;

    public bool IsLeAudio => _leAudio;

    public event EventHandler? TransportChanged;

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
        try
        {
            return new BluetoothLinkMonitor(device, await BondedLeIdentitiesAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The bonded LE identity at the public address plus the other bonded earbud of the pair (either
    /// may be the only one out of the case). An unpaired GATT link (e.g. GAIA control) is not audio.
    /// </summary>
    private static async Task<ImmutableArray<BluetoothLEDevice>> BondedLeIdentitiesAsync(ulong address, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? le;
        try
        {
            le = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
        {
            return [];
        }
        if (le is null) return [];
        bool bonded;
        try
        {
            bonded = await LeAudioLink.IsBondedAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            le.Dispose();
            throw;
        }
        if (!bonded)
        {
            le.Dispose();
            return [];
        }
        try
        {
            return [le, .. await LeAudioPairing.FindSecondaryBondsAsync(address, le.Name, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
        {
            return [le];
        }
    }

    /// <summary>Looks for the other earbud's bond until it appears, the attempts run out or the monitor is disposed.</summary>
    private async Task FindSecondaryAsync(int attempts)
    {
        if (Interlocked.Exchange(ref _searching, 1) != 0) return;
        try
        {
            var token = _lifetime.Token;
            for (int i = 0; i < attempts && !token.IsCancellationRequested; i++)
            {
                if (i > 0) await Task.Delay(SecondaryRetry, token).ConfigureAwait(false);
                var current = _leDevices;
                if (current.Length != 1) return;
                List<BluetoothLEDevice> found;
                try
                {
                    found = await LeAudioPairing.FindSecondaryBondsAsync(current[0].BluetoothAddress, current[0].Name, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException or ObjectDisposedException)
                {
                    continue;
                }
                if (found.Count == 0) continue;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _leDevices = _leDevices.AddRange(found);
                        foreach (var le in found) le.ConnectionStatusChanged += OnConnectionStatusChanged;
                        found.Clear();
                    }
                }
                foreach (var le in found) le.Dispose();
                OnConnectionStatusChanged(this, EventArgs.Empty);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _searching, 0);
        }
    }

    private bool Evaluate() => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected || EvaluateLe();

    private bool EvaluateLe() => _leDevices.Any(le => le.ConnectionStatus == BluetoothConnectionStatus.Connected);

    private void OnConnectionStatusChanged(object sender, object args)
    {
        bool connected, leAudio;
        try { connected = Evaluate(); leAudio = EvaluateLe(); }
        catch (ObjectDisposedException) { return; }
        if (sender is BluetoothLEDevice && _leDevices.Length == 1) _ = FindSecondaryAsync(1);
        if (leAudio != _leAudio)
        {
            _leAudio = leAudio;
            TransportChanged?.Invoke(this, EventArgs.Empty);
        }
        if (connected == _connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(this, connected);
    }

    public void Dispose()
    {
        ImmutableArray<BluetoothLEDevice> leDevices;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            leDevices = _leDevices;
            _leDevices = [];
        }
        _lifetime.Cancel();
        foreach (var le in leDevices)
        {
            le.ConnectionStatusChanged -= OnConnectionStatusChanged;
            le.Dispose();
        }
        var device = Interlocked.Exchange(ref _device, null);
        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();
        }
        // _lifetime is left undisposed (no timer): a late FindSecondaryAsync may still read its token.
    }
}
