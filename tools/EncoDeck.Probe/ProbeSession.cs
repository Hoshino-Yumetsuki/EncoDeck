// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Probe;

/// <summary>Opens the control channel for Probe commands.</summary>
internal static class ProbeSession
{
    public static async Task<(OppoSession Session, PairedDeviceInfo Device, Guid Service)?> OpenAsync(ProbeOptions options, ConsoleTrace trace, CancellationToken ct)
    {
        var catalog = new BluetoothDeviceCatalog();
        var device = await DeviceSelector.SelectAsync(catalog, options, ct);
        if (device is null) return null;
        if (!device.IsConnected)
        {
            Console.Error.WriteLine($"{device.Name} is paired but not connected. Connect it in Windows Bluetooth settings first.");
            return null;
        }

        IReadOnlyList<Guid> uuids = options.Get("uuid") is { } custom ? [Guid.Parse(custom)] : OppoServiceUuids.Candidates;
        Console.WriteLine($"Opening control channel to {device.Name} ({device.Address}) ...");
        var connector = new RfcommChannelConnector(new DeviceProfileRegistry(BuiltInProfiles.Create()));
        var result = await connector.OpenAsync(device.DeviceId, uuids, ct);
        if (result.Status != ChannelOpenStatus.Opened)
        {
            Console.Error.WriteLine($"Control channel not opened: {result.Status} {result.Detail}");
            return null;
        }
        Console.WriteLine($"Opened {result.ServiceUuid:D}.");
        var session = new OppoSession(result.Channel!, trace: trace);
        session.Start();
        return (session, device, result.ServiceUuid!.Value);
    }
}
