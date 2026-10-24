// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>LE Audio state of a Classic-paired device's public address.</summary>
public static class LeAudioLink
{
    private static readonly ConcurrentDictionary<ulong, byte> PairingChanges = new();

    internal static bool IsPairingChanging(ulong address) => PairingChanges.ContainsKey(address);

    internal static IDisposable BeginPairingChange(ulong address)
    {
        if (!PairingChanges.TryAdd(address, 0)) throw new InvalidOperationException("A pairing change is already in progress for these earbuds.");
        return new PairingChange(address);
    }

    private sealed class PairingChange(ulong address) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) PairingChanges.TryRemove(address, out _);
        }
    }

    /// <summary>
    /// True when Windows has a bond with the LE identity at the same public address, i.e. the
    /// earbuds were switched to LE Audio on this PC (connected or not). Read the paired association
    /// endpoints, not a newly opened device's transient IsPaired snapshot during reconnect.
    /// Errors propagate: an unknown bond state must never authorize a Classic connection.
    /// </summary>
    public static async Task<bool> IsBondedAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        string selector = $"({BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(bluetoothAddress, BluetoothAddressType.Public)}) AND ({BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)})";
        var bonds = await DeviceInformation.FindAllAsync(selector, [], DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return bonds.Count > 0;
    }

    /// <summary>
    /// True when Windows has a bond with the LE identity at the same public address and that earbud
    /// or the other bonded one is connected (earbuds on LE Audio keep only LE links; Classic stays down).
    /// </summary>
    public static async Task<bool> IsConnectedAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
            if (device is null || !await IsBondedAsync(bluetoothAddress, cancellationToken).ConfigureAwait(false)) return false;
            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;
            var others = await LeAudioPairing.FindSecondaryBondsAsync(bluetoothAddress, device.Name, cancellationToken).ConfigureAwait(false);
            bool any = others.Any(o => o.ConnectionStatus == BluetoothConnectionStatus.Connected);
            foreach (var other in others) other.Dispose();
            return any;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
