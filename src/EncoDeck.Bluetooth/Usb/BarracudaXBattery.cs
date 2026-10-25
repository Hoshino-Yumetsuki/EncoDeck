// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Foundation;

namespace EncoDeck.Bluetooth.Usb;

public sealed record UsbHeadsetStatus(int? Level = null, bool? IsCharging = null, bool? IsConnected = null);

internal static class BarracudaXBattery
{
    public static async Task<UsbHeadsetStatus> ReadAsync(HidDevice device, CancellationToken ct)
    {
        if (!BarracudaXProtocol.IsCollection(device.VendorId, device.ProductId, device.UsagePage, device.UsageId)) return new();
        var reports = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = false });
        var decoder = new BarracudaXDecoder();
        TypedEventHandler<HidDevice, HidInputReportReceivedEventArgs> handler = (_, args) => reports.Writer.TryWrite(args.Report.Data.ToArray());
        byte sequence = 0;
        async Task SendAsync(byte[] data, CancellationToken token)
        {
            var report = device.CreateOutputReport(1);
            if (report.Data.Length != 64) throw new IOException("Unexpected Barracuda HID report length");
            report.Data = data.AsBuffer();
            if (await device.SendOutputReportAsync(report).AsTask(token).WaitAsync(TimeSpan.FromMilliseconds(800), token).ConfigureAwait(false) != 64)
                throw new IOException("Incomplete Barracuda HID report");
        }
        async Task<byte[]?> WaitAsync(Func<byte[], bool> match, CancellationToken token, bool handshake = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(800);
            try
            {
                while (true)
                {
                    byte[] report = await reports.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                    if (handshake)
                    {
                        if (report.Length >= 4 && report[0] == 1 && report[1] == 0x40 && report[2] == 1 && report[3] == 1) return report;
                        continue;
                    }
                    foreach (byte[] frame in decoder.Feed(report))
                    {
                        if (match(frame)) return frame;
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        }
        async Task<bool> RouteAsync(bool remote, CancellationToken token)
        {
            byte seq = ++sequence;
            await SendAsync(BarracudaXProtocol.Query(seq, 0xE1, remote), token).ConfigureAwait(false);
            return await WaitAsync(f => BarracudaXProtocol.IsAck(f, seq), token).ConfigureAwait(false) is not null;
        }
        bool restore = false;
        device.InputReportReceived += handler;
        try
        {
            await SendAsync(BarracudaXProtocol.Handshake(), ct).ConfigureAwait(false);
            if (await WaitAsync(_ => false, ct, handshake: true).ConfigureAwait(false) is null) return new();
            byte seq = ++sequence;
            await SendAsync(BarracudaXProtocol.Query(seq, 0xE3), ct).ConfigureAwait(false);
            // A data frame's sequence is a device counter. Only the ACK echoes our sequence.
            bool acknowledged = false;
            byte[]? link = await WaitAsync(f =>
            {
                if (BarracudaXProtocol.IsAck(f, seq)) acknowledged = true;
                return acknowledged && BarracudaXProtocol.Link(f).HasValue;
            }, ct).ConfigureAwait(false);
            if (link is null) return new();
            if (BarracudaXProtocol.Link(link) != true) return new(IsConnected: false);
            // Do not take a remote route already owned by another diagnostic application.
            await SendAsync(BarracudaXProtocol.Query(++sequence, 0xE0), ct).ConfigureAwait(false);
            byte[]? route = await WaitAsync(f => BarracudaXProtocol.Route(f).HasValue, ct).ConfigureAwait(false);
            if (route is null || BarracudaXProtocol.Route(route) != 0) return new(IsConnected: true);
            restore = true; // Even a cancelled or timed-out route selection must restore local mode.
            if (!await RouteAsync(true, ct).ConfigureAwait(false)) return new(IsConnected: true);
            await SendAsync(BarracudaXProtocol.Query(++sequence, 0x21), ct).ConfigureAwait(false);
            byte[]? battery = await WaitAsync(f => BarracudaXProtocol.Value(f, 0x21).HasValue, ct).ConfigureAwait(false);
            await SendAsync(BarracudaXProtocol.Query(++sequence, 0x2A), ct).ConfigureAwait(false);
            byte[]? cable = await WaitAsync(f => BarracudaXProtocol.Value(f, 0x2A).HasValue, ct).ConfigureAwait(false);
            return new(battery is null ? null : BarracudaXProtocol.Value(battery, 0x21),
                cable is null ? null : BarracudaXProtocol.Value(cable, 0x2A) == 1, true);
        }
        finally
        {
            if (restore)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await RouteAsync(false, cleanup.Token).ConfigureAwait(false);
                    await SendAsync(BarracudaXProtocol.Query(++sequence, 0xE0), cleanup.Token).ConfigureAwait(false);
                    await WaitAsync(f => BarracudaXProtocol.Route(f) == 0, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            device.InputReportReceived -= handler;
        }
    }
}
