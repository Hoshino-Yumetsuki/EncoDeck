// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Foundation;

namespace EncoDeck.Bluetooth.Usb;

internal static class UsbVendorBattery
{
    public static async Task<int?> ReadAsync(HidDevice device, CancellationToken ct)
    {
        ushort vendor = device.VendorId, product = device.ProductId;
        if (!UsbVendorBatteryProtocol.IsCollection(vendor, product, device.UsagePage, device.UsageId)) return null;
        byte[] query = UsbVendorBatteryProtocol.Query(vendor, product);
        if (vendor == 0x1532 && product == 0x0528)
        {
            var feature = device.CreateFeatureReport(0xFF);
            if (feature.Data.Length != 64) return null;
            feature.Data = query.AsBuffer();
            await device.SendFeatureReportAsync(feature).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var reply = await device.GetFeatureReportAsync(0xFF).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (UsbVendorBatteryProtocol.Parse(vendor, product, reply.Data.ToArray()) is { } level) return level;
            }
            return null;
        }

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<HidDevice, HidInputReportReceivedEventArgs> handler = (_, args) =>
        {
            if (UsbVendorBatteryProtocol.Parse(vendor, product, args.Report.Data.ToArray()) is { } level) completion.TrySetResult(level);
        };
        bool remote = vendor == 0x1532;
        async Task SendAsync(byte[] data, CancellationToken token)
        {
            var report = device.CreateOutputReport(data[0]);
            if (report.Data.Length != 64) throw new InvalidOperationException("Unexpected HID report length");
            report.Data = data.AsBuffer();
            uint written = await device.SendOutputReportAsync(report).AsTask(token).WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            if (written != 64) throw new IOException("Incomplete HID output report");
        }
        device.InputReportReceived += handler;
        try
        {
            if (remote)
            {
                await SendAsync(UsbVendorBatteryProtocol.Remote(true), ct).ConfigureAwait(false);
                await Task.Delay(35, ct).ConfigureAwait(false);
            }
            for (int attempt = 0; attempt < (remote ? 3 : 1); attempt++)
            {
                if (remote)
                {
                    await SendAsync(UsbVendorBatteryProtocol.Remote(true), ct).ConfigureAwait(false);
                    await Task.Delay(35, ct).ConfigureAwait(false);
                }
                await SendAsync(query, ct).ConfigureAwait(false);
                try { return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(remote ? 200 : 1000), ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            return null;
        }
        finally
        {
            device.InputReportReceived -= handler;
            if (remote)
            {
                // Release PA remote mode even when the caller cancels or the link stops responding.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await SendAsync(UsbVendorBatteryProtocol.Remote(false), cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }
    }
}
