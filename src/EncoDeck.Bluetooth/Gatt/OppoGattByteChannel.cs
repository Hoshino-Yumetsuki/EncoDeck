// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// OPPO control channel over LE Audio: GATT service 079A, write 0100079A, notify 0200079A.
/// Frames are the same as over RFCOMM, so notifications are passed on as one byte stream.
/// Both earbuds expose the service but only the current primary answers, and the roles swap
/// (e.g. after one earbud went into the case), so every bonded earbud of the pair is opened:
/// writes go to all of them and notifications from any are read. Verified on the Enco X4.
/// </summary>
internal sealed class OppoGattByteChannel : IDuplexByteChannel
{
    public static readonly Guid ServiceId = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid WriteId = new("0100079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid NotifyId = new("0200079A-D102-11E1-9B23-00025B00A5A5");

    private sealed class Endpoint(BluetoothLEDevice device, GattSession? session, GattDeviceService service, GattCharacteristic write, GattCharacteristic notify)
    {
        public BluetoothLEDevice Device { get; } = device;
        public GattSession? Session { get; } = session;
        public GattDeviceService Service { get; } = service;
        public GattCharacteristic Write { get; } = write;
        public GattCharacteristic Notify { get; } = notify;
        // Control commands are rare; prefer acknowledged writes so a dropped command surfaces.
        public GattWriteOption WriteOption { get; } = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        public bool Subscribed { get; set; }
        public bool IsConnected => Device.ConnectionStatus == BluetoothConnectionStatus.Connected;
    }

    private readonly List<Endpoint> _endpoints = [];
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private byte[]? _pending;
    private int _pendingOffset;
    private int _stopped;
    private int _disposed;

    private OppoGattByteChannel()
    {
        _shutdownToken = _shutdown.Token;
    }

    public string Description => $"OPPO GATT 079A ({_endpoints.Count} earbud(s))";

    /// <summary>
    /// Opens the channel on the bonded LE identity at <paramref name="bluetoothAddress"/> and on the
    /// other bonded earbud of the pair. Unpaired or disconnected earbuds are skipped: this never pairs or pages.
    /// </summary>
    internal static async Task<ChannelOpenResult> OpenAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        var channel = new OppoGattByteChannel();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ChannelOpenResult? failure = null;
        try
        {
            var primary = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, BluetoothAddressType.Public).AsTask(timeout.Token).ConfigureAwait(false);
            if (primary is null || !primary.DeviceInformation.Pairing.IsPaired)
            {
                primary?.Dispose();
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The earbuds are not paired over LE Audio.");
            }
            var candidates = new List<BluetoothLEDevice> { primary };
            candidates.AddRange(await LeAudioPairing.FindSecondaryBondsAsync(bluetoothAddress, primary.Name, timeout.Token).ConfigureAwait(false));
            foreach (var device in candidates)
            {
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    device.Dispose();
                    continue;
                }
                var result = await channel.AddEndpointAsync(device, timeout.Token).ConfigureAwait(false);
                if (result is not null) failure = result;
            }
            if (channel._endpoints.Count == 0)
                return failure ?? ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The earbuds are not connected over LE Audio.");
            var opened = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, ServiceId, null);
            channel = null;
            return opened;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "OPPO GATT connection timed out or disconnected.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"OPPO GATT: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Takes ownership of <paramref name="device"/>; returns a failure, or null once the earbud's endpoint is subscribed.</summary>
    private async Task<ChannelOpenResult?> AddEndpointAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        GattSession? session = null;
        GattDeviceService? service = null;
        try
        {
            var access = await device.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"BLE device access: {access}");
            // Keep the LE link while the channel is open, as the RFCOMM socket keeps the ACL.
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(cancellationToken).ConfigureAwait(false);
            if (session is not null) session.MaintainConnection = true;

            var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < services.Services.Count; i++)
            {
                if (i == 0) service = services.Services[i];
                else services.Services[i].Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success) return Failed(services.Status, "OPPO GATT service discovery");
            if (service is null) return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "OPPO GATT service not found.");

            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success) return Failed(characteristics.Status, "OPPO GATT characteristic discovery");
            var write = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == WriteId);
            var notify = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == NotifyId);
            if (write is null || notify is null || !notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                || (write.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "OPPO GATT write or notify characteristic is missing.");

            var endpoint = new Endpoint(device, session, service, write, notify);
            _endpoints.Add(endpoint);
            session = null;
            service = null;
            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            notify.ValueChanged += OnValueChanged;
            endpoint.Subscribed = true;
            var status = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (status == GattCommunicationStatus.Success) return null;
            await RemoveAsync(endpoint).ConfigureAwait(false);
            return Failed(status, "OPPO GATT subscription");
        }
        finally
        {
            service?.Dispose();
            session?.Dispose();
            if (!_endpoints.Any(e => ReferenceEquals(e.Device, device))) device.Dispose();
        }
    }

    private static ChannelOpenResult Failed(GattCommunicationStatus status, string operation) =>
        ChannelOpenResult.Failed(status switch
        {
            GattCommunicationStatus.AccessDenied => ChannelOpenStatus.AccessDenied,
            GattCommunicationStatus.Unreachable => ChannelOpenStatus.DeviceUnreachable,
            _ => ChannelOpenStatus.ConnectFailed,
        }, $"{operation}: {status}");

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref _stopped) == 0 && args.CharacteristicValue.Length > 0)
            _notifications.Writer.TryWrite(args.CharacteristicValue.ToArray());
    }

    /// <summary>One earbud going into the case is fine; the channel ends when none is connected.</summary>
    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool any;
        try { any = _endpoints.Any(e => e.IsConnected); }
        catch (ObjectDisposedException) { any = false; }
        if (!any) Stop();
    }

    private void Stop()
    {
        lock (_shutdown)
        {
            if (_stopped != 0) return;
            Volatile.Write(ref _stopped, 1);
            _shutdown.Cancel();
            _notifications.Writer.TryComplete();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) return 0;
        // A byte stream like RFCOMM: the frame decoder reassembles frames across notifications.
        while (_pending is null)
        {
            if (!await _notifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (_shutdownToken.IsCancellationRequested) return 0;
            if (_notifications.Reader.TryRead(out var next)) { _pending = next; _pendingOffset = 0; }
        }
        int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
        _pendingOffset += count;
        if (_pendingOffset == _pending.Length) _pending = null;
        return count;
    }

    /// <summary>Writes to every connected earbud; only the current primary answers. Fails only if no write succeeds.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_shutdownToken.IsCancellationRequested) throw new IOException("OPPO GATT earbuds are disconnected.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        GattCommunicationStatus last = GattCommunicationStatus.Unreachable;
        bool delivered = false;
        foreach (var endpoint in _endpoints)
        {
            if (!endpoint.IsConnected) continue;
            last = await WriteToAsync(endpoint, data, linked.Token).ConfigureAwait(false);
            delivered |= last == GattCommunicationStatus.Success;
        }
        if (!delivered) throw new IOException($"OPPO GATT write failed: {last}");
    }

    private static async Task<GattCommunicationStatus> WriteToAsync(Endpoint endpoint, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // ATT carries at most MTU - 3 bytes per write.
        int chunk = Math.Max(20, (endpoint.Session?.MaxPduSize ?? 23) - 3);
        for (int offset = 0; offset < data.Length; offset += chunk)
        {
            var part = data.Slice(offset, Math.Min(chunk, data.Length - offset));
            IBuffer buffer = MemoryMarshal.TryGetArray(part, out var segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : part.ToArray().AsBuffer();
            GattCommunicationStatus status;
            try
            {
                status = await endpoint.Write.WriteValueAsync(buffer, endpoint.WriteOption).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                return GattCommunicationStatus.Unreachable;
            }
            if (status != GattCommunicationStatus.Success) return status;
        }
        return GattCommunicationStatus.Success;
    }

    private async Task RemoveAsync(Endpoint endpoint)
    {
        _endpoints.Remove(endpoint);
        await ReleaseAsync(endpoint).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(Endpoint endpoint)
    {
        endpoint.Device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        endpoint.Notify.ValueChanged -= OnValueChanged;
        try
        {
            if (endpoint.Subscribed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await endpoint.Notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or COMException)
                {
                    // The link may already be gone; local handles still must close.
                }
            }
        }
        finally
        {
            try { endpoint.Service.Dispose(); }
            finally
            {
                try { endpoint.Session?.Dispose(); }
                finally { endpoint.Device.Dispose(); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try
        {
            foreach (var endpoint in _endpoints) await ReleaseAsync(endpoint).ConfigureAwait(false);
            _endpoints.Clear();
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
