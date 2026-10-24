// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace EncoDeck.Bluetooth.Rfcomm;

/// <summary>Opens the first matching control service over RFCOMM, GAIA GATT or OPPO GATT.</summary>
public sealed class RfcommChannelConnector(IDeviceProfileRegistry profiles, ILogger<RfcommChannelConnector>? logger = null) : IChannelConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly ILogger _logger = logger ?? NullLogger<RfcommChannelConnector>.Instance;

    public async Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
    {
        ChannelOpenResult? gattFailure = null, connectFailure = null;
        BluetoothDevice? device;
        try
        {
            device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, ex.Message);
        }
        if (device is null)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "Device not found.");
        }

        using (device)
        {
            var access = await device.RequestAccessAsync().AsTask(cancellationToken);
            if (access != DeviceAccessStatus.Allowed)
            {
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"Device access: {access}");
            }

            var identity = new DeviceIdentity(deviceId, device.Name, new BluetoothAddress(device.BluetoothAddress));
            if (serviceUuids.Contains(MoondropConstants.SppServiceId) &&
                profiles.Match(identity) is MoondropProfile { RfcommChannel: { } channel })
            {
                var result = await BluetoothSocketByteChannel.OpenAsync(device.BluetoothAddress, channel, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                connectFailure = result;
                _logger.LogInformation("Known MOONDROP RFCOMM channel failed: {Detail}", result.Detail);
            }

            // OPPO earbuds switched to LE Audio keep only an LE link; their control moves to GATT 079A.
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected && serviceUuids.Any(OppoServiceUuids.IsCandidate))
            {
                var result = await OppoGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                _logger.LogInformation("OPPO GATT connect failed: {Detail}", result.Detail);
                // On LE Audio, RFCOMM would page the Classic link that Windows keeps down (and disturb
                // the LE links); report the GATT failure so the supervisor retries instead.
                if (result.Status != ChannelOpenStatus.DeviceUnreachable || await LeAudioLink.IsConnectedAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false))
                    return result.Status is ChannelOpenStatus.AccessDenied or ChannelOpenStatus.ServiceNotFound
                        ? ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, result.Detail ?? "OPPO GATT")
                        : result;
                gattFailure = result;
            }

            foreach (var uuid in serviceUuids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (uuid == SennheiserProfile.GattServiceId)
                {
                    var result = await GaiaGattByteChannel.OpenSennheiserAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                    if (result.Status == ChannelOpenStatus.Opened) return result;
                    gattFailure = result;
                    _logger.LogInformation("Sennheiser GAIA GATT connect failed: {Detail}", result.Detail);
                    continue;
                }
                if (uuid == MoondropConstants.GattServiceId)
                {
                    var result = await GaiaGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                    if (result.Status == ChannelOpenStatus.Opened) return result;
                    gattFailure = result;
                    _logger.LogInformation("GAIA GATT connect failed: {Detail}", result.Detail);
                    continue;
                }
                RfcommDeviceServicesResult services;
                try
                {
                    // Cached first: avoids a radio round-trip when Windows already has the SDP record.
                    services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(uuid), BluetoothCacheMode.Cached).AsTask(cancellationToken);
                    if (services.Services.Count == 0)
                    {
                        services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(uuid), BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("SDP lookup for {Service} failed: 0x{HResult:X8}", uuid, ex.HResult);
                    continue;
                }

                if (services.Error != BluetoothError.Success)
                {
                    _logger.LogInformation("SDP lookup for {Service}: {Error}", uuid, services.Error);
                    if (services.Error is BluetoothError.DeviceNotConnected or BluetoothError.RadioNotAvailable)
                    {
                        if (services.Error == BluetoothError.RadioNotAvailable || !serviceUuids.Contains(SennheiserProfile.GattServiceId))
                            return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, services.Error.ToString());
                    }
                    continue;
                }

                foreach (var service in services.Services)
                {
                    var result = await TryConnectAsync(service, uuid, cancellationToken).ConfigureAwait(false);
                    service.Dispose();
                    if (result.Status == ChannelOpenStatus.Opened) return result;
                    connectFailure = result;
                    _logger.LogInformation("RFCOMM connect to {Service} failed: {Detail}", uuid, result.Detail);
                }
            }
        }

        // A control socket that exists but failed to connect is usually busy right after audio
        // connects; report it (retryable) rather than a permanent GATT or SDP outcome.
        return connectFailure ?? gattFailure ?? ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "No candidate control service could be opened.");
    }

    private static async Task<ChannelOpenResult> TryConnectAsync(RfcommDeviceService service, Guid uuid, CancellationToken cancellationToken)
    {
        var socket = new StreamSocket();
        socket.Control.KeepAlive = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(
                    service.ConnectionHostName,
                    service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)
                .AsTask(timeout.Token);
            var channel = new RfcommByteChannel(socket, $"RFCOMM {uuid:D}");
            return new ChannelOpenResult(ChannelOpenStatus.Opened, channel, uuid, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "Connect timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            var status = SocketError.GetStatus(ex.HResult) switch
            {
                SocketErrorStatus.HostIsDown or SocketErrorStatus.HostNotFound or SocketErrorStatus.ConnectionTimedOut => ChannelOpenStatus.DeviceUnreachable,
                _ => ChannelOpenStatus.ConnectFailed,
            };
            return ChannelOpenResult.Failed(status, $"{SocketError.GetStatus(ex.HResult)} (0x{ex.HResult:X8})");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
