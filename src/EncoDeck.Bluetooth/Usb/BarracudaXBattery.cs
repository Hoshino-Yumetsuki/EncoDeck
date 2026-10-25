// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Foundation;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.Usb;

public sealed record UsbHeadsetStatus(int? Level = null, bool? IsCharging = null, bool? IsConnected = null);

internal static class BarracudaXBattery
{
    public static async Task<UsbHeadsetStatus> ReadAsync(HidDevice device, CancellationToken ct, ILogger? logger = null)
    {
        if (!BarracudaXProtocol.IsCollection(device.VendorId, device.ProductId, device.UsagePage, device.UsageId)) return new();
        var reports = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = false });
        TypedEventHandler<HidDevice, HidInputReportReceivedEventArgs> handler = (_, args) => reports.Writer.TryWrite(args.Report.Data.ToArray());
        bool replacement = device.ProductId == 0x0550;
        async Task SendAsync(byte[] data, CancellationToken token)
        {
            var report = device.CreateOutputReport(data[0]);
            if (report.Data.Length != 64) throw new IOException("Unexpected Barracuda HID report length");
            report.Data = data.AsBuffer();
            if (await device.SendOutputReportAsync(report).AsTask(token).WaitAsync(TimeSpan.FromMilliseconds(800), token).ConfigureAwait(false) != 64)
                throw new IOException("Incomplete Barracuda HID report");
            if (replacement)
            {
                var status = await device.GetInputReportAsync(2).AsTask(token).WaitAsync(TimeSpan.FromMilliseconds(1200), token).ConfigureAwait(false);
                byte[] bytes = status.Data.ToArray();
                if (bytes.Length != 64 || bytes[0] != 2 || bytes[1] != 0x4F)
                    throw new IOException("Barracuda replacement receiver did not acknowledge HID output");
            }
        }
        device.InputReportReceived += handler;
        try
        {
            return await ReadReportsAsync(SendAsync, token => reports.Reader.ReadAsync(token).AsTask(), replacement, ct, logger).ConfigureAwait(false);
        }
        finally { device.InputReportReceived -= handler; }
    }

    internal static async Task<UsbHeadsetStatus> ReadReportsAsync(
        Func<byte[], CancellationToken, Task> send,
        Func<CancellationToken, Task<byte[]>> receive,
        bool replacement, CancellationToken ct, ILogger? logger = null)
    {
        var decoder = new BarracudaXDecoder();
        var pending = new Queue<byte[]>();
        byte sequence = 0;
        string stage = "handshake";
        Task SendAsync(byte[] data, CancellationToken token) => send(data, token);
        byte[] Query(byte seq, byte command, bool remote = false) => replacement
            ? BarracudaXProtocol.ReplacementQuery(seq, command, remote)
            : BarracudaXProtocol.Query(seq, command, remote);
        async Task<byte[]?> WaitAsync(Func<byte[], bool> match, CancellationToken token, bool handshake = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(1200);
            try
            {
                while (true)
                {
                    while (pending.TryDequeue(out var frame))
                        if (match(frame)) return frame;
                    byte[] report = await receive(timeout.Token).ConfigureAwait(false);
                    if (handshake)
                    {
                        if (report.Length >= 4 && report[0] == 1 && report[1] == 0x40 && report[2] == 1 && report[3] == 1) return report;
                        continue;
                    }
                    foreach (byte[] frame in decoder.Feed(report, replacement)) pending.Enqueue(frame);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { logger?.LogWarning("Barracuda X query timed out at {Stage}", stage); return null; }
        }
        async Task<bool> RouteAsync(bool remote, CancellationToken token)
        {
            byte seq = ++sequence;
            await SendAsync(Query(seq, 0xE1, remote), token).ConfigureAwait(false);
            return await WaitAsync(f => BarracudaXProtocol.IsAck(f, seq), token).ConfigureAwait(false) is not null;
        }
        bool restore = false;
        try
        {
            await SendAsync(replacement ? BarracudaXProtocol.ReplacementHandshake() : BarracudaXProtocol.Handshake(), ct).ConfigureAwait(false);
            if (!replacement && await WaitAsync(_ => false, ct, handshake: true).ConfigureAwait(false) is null) return new();
            stage = "wireless link (E3)";
            byte seq = ++sequence;
            await SendAsync(Query(seq, 0xE3), ct).ConfigureAwait(false);
            // A data frame's sequence is a device counter. Only the ACK echoes our sequence.
            bool acknowledged = false;
            byte[]? linkReply = null;
            byte[]? link = await WaitAsync(f =>
            {
                if (BarracudaXProtocol.IsAck(f, seq)) acknowledged = true;
                if (BarracudaXProtocol.Link(f).HasValue) linkReply = f;
                return acknowledged && linkReply is not null;
            }, ct).ConfigureAwait(false);
            if (link is null) return new();
            if (BarracudaXProtocol.Link(linkReply!) != true) return new(IsConnected: false);
            // Do not take a remote route already owned by another diagnostic application.
            stage = "local route (E0)";
            await SendAsync(Query(++sequence, 0xE0), ct).ConfigureAwait(false);
            byte[]? route = await WaitAsync(f => BarracudaXProtocol.Route(f).HasValue, ct).ConfigureAwait(false);
            if (route is null || BarracudaXProtocol.Route(route) != 0) return new(IsConnected: true);
            restore = true; // Even a cancelled or timed-out route selection must restore local mode.
            stage = "select headset route (E1)";
            if (!await RouteAsync(true, ct).ConfigureAwait(false)) return new(IsConnected: true);
            stage = "battery (21)";
            await SendAsync(Query(++sequence, 0x21), ct).ConfigureAwait(false);
            byte[]? battery = await WaitAsync(f => BarracudaXProtocol.Value(f, 0x21).HasValue, ct).ConfigureAwait(false);
            stage = "charging cable (2A)";
            await SendAsync(Query(++sequence, 0x2A), ct).ConfigureAwait(false);
            byte[]? cable = await WaitAsync(f => BarracudaXProtocol.Value(f, 0x2A).HasValue, ct).ConfigureAwait(false);
            return new(battery is null ? null : BarracudaXProtocol.Value(battery, 0x21),
                cable is null ? null : BarracudaXProtocol.Value(cable, 0x2A) == 1, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger?.LogWarning("Barracuda X query failed at {Stage}: 0x{HResult:X8}", stage, ex.HResult);
            throw;
        }
        finally
        {
            if (restore)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    stage = "restore local route";
                    await RouteAsync(false, cleanup.Token).ConfigureAwait(false);
                    await SendAsync(Query(++sequence, 0xE0), cleanup.Token).ConfigureAwait(false);
                    if (await WaitAsync(f => BarracudaXProtocol.Route(f) == 0, cleanup.Token).ConfigureAwait(false) is null)
                        logger?.LogWarning("Barracuda X local route restoration was not confirmed");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { logger?.LogWarning("Barracuda X local route restoration failed: 0x{HResult:X8}", ex.HResult); }
            }
        }
    }
}
