// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Bluetooth.Sdp;
using EncoDeck.Core.Privacy;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>Snapshot of a paired Bluetooth Classic (BR/EDR) device as reported by Windows.</summary>
public sealed record PairedDeviceInfo(
    string DeviceId,
    string Name,
    BluetoothAddress Address,
    bool IsConnected,
    uint ClassOfDeviceRaw);

/// <summary>One RFCOMM service exposed by a device's SDP database.</summary>
public sealed record RfcommServiceInfo(
    Guid ServiceUuid,
    string ServiceId,
    SdpRecordInfo? Record,
    string? SdpError);

public sealed record RfcommServiceQueryResult(
    BluetoothError Error,
    IReadOnlyList<RfcommServiceInfo> Services);

/// <summary>
/// Read-only enumeration of paired classic devices and their RFCOMM services.
/// Uses only public WinRT APIs; nothing here writes to a device.
/// </summary>
public sealed class BluetoothDeviceCatalog
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    public async Task<IReadOnlyList<PairedDeviceInfo>> GetPairedClassicDevicesAsync(CancellationToken cancellationToken = default)
    {
        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector, [IsConnectedProperty]).AsTask(cancellationToken);
        var result = new List<PairedDeviceInfo>(infos.Count);
        foreach (var info in infos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothDevice? device;
            try
            {
                // The pairing-state selector can also yield BLE association endpoints, which
                // BluetoothDevice.FromIdAsync rejects with E_INVALIDARG; those are not classic devices.
                device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(cancellationToken);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (device is null) continue;
            using var _ = device;
            result.Add(new PairedDeviceInfo(
                info.Id,
                device.Name,
                new BluetoothAddress(device.BluetoothAddress),
                device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                device.ClassOfDevice?.RawValue ?? 0));
        }
        return result;
    }

    public async Task<RfcommServiceQueryResult> GetRfcommServicesAsync(
        string deviceId,
        bool uncached,
        bool includeSdpRecords,
        CancellationToken cancellationToken = default)
    {
        using var device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken)
            ?? throw new InvalidOperationException("Bluetooth device not found.");
        var mode = uncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
        var services = await device.GetRfcommServicesAsync(mode).AsTask(cancellationToken);

        var list = new List<RfcommServiceInfo>(services.Services.Count);
        foreach (var service in services.Services)
        {
            using (service)
            {
                SdpRecordInfo? record = null;
                string? sdpError = null;
                if (includeSdpRecords)
                {
                    try
                    {
                        var raw = await service.GetSdpRawAttributesAsync(mode).AsTask(cancellationToken);
                        var copy = raw.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
                        record = SdpRecordInfo.FromRawAttributes(copy);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        sdpError = $"{ex.GetType().Name}: 0x{ex.HResult:X8}";
                    }
                }
                list.Add(new RfcommServiceInfo(service.ServiceId.Uuid, service.ServiceId.AsString(), record, sdpError));
            }
        }
        return new RfcommServiceQueryResult(services.Error, list);
    }
}
