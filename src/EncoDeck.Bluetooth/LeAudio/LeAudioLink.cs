// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Bluetooth;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>LE Audio state of a Classic-paired device's public address.</summary>
public static class LeAudioLink
{
    /// <summary>
    /// True when Windows has a bond with the LE identity at the same public address and that earbud
    /// or the other bonded one is connected (earbuds on LE Audio keep only LE links; Classic stays down).
    /// </summary>
    public static async Task<bool> IsConnectedAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
            if (device is null || !device.DeviceInformation.Pairing.IsPaired) return false;
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
