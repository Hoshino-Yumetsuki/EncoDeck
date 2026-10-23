// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Usb;

namespace EncoDeck.Probe;

internal static class UsbDevicesCommand
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        using var watcher = new UsbHeadsetWatcher();
        watcher.Start();
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < until) await Task.Delay(100, ct).ConfigureAwait(false);
        var devices = watcher.Devices;
        if (devices.Count == 0)
        {
            Console.WriteLine("No verified supported USB audio receivers found. Proprietary headset controls are not inferred.");
            return 3;
        }
        foreach (var device in devices)
        {
            int? battery = watcher.Container(device.Identity.DeviceId) is { } container ? await UsbHidBattery.ReadAsync(container, ct).ConfigureAwait(false) : null;
            Console.WriteLine($"{device.Identity.Name}: receiver present; standard HID battery = {battery?.ToString() ?? "unavailable"}; headset radio link unconfirmed");
        }
        return 0;
    }
}
