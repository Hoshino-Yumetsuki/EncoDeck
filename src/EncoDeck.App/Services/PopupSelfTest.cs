// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using EncoDeck.App.Views;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Platform.Shell;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EncoDeck.App.Services;

/// <summary>
/// <c>--popup-selftest &lt;dir&gt;</c>: shows the connection pop-up <b>off-screen</b> (nothing appears
/// on any monitor, focus is never taken), checks with the system's own hit test that it receives the
/// mouse normally and lets all mouse input through in click-through mode, captures only its own
/// window at points on the official timeline, writes <c>result.json</c> and exits. It runs before the
/// single-instance check and never starts the Bluetooth runtime, so a running EncoDeck is untouched.
/// </summary>
internal static class PopupSelfTest
{
    public const string Argument = "--popup-selftest";

    private static readonly DeviceIdentity SampleDevice = new("selftest", "OPPO Enco X4", new BluetoothAddress(0));

    /// <param name="quick">Only the hit tests (about a second), no timeline captures.</param>
    public static async Task<int> RunAsync(App app, string outDir, DispatcherQueue dispatcher, bool quick)
    {
        Directory.CreateDirectory(outDir);
        var result = new Dictionary<string, object?>();
        try
        {
            var host = AppHost.Create(Path.Combine(outDir, "logs"));
            app.SetHostForDiagnostics(host);

            // Display-only sample state (never shown to the user): the X4 with typical levels.
            var now = DateTimeOffset.UtcNow;
            Observed<BatteryReading> Level(int level, bool charging = false) => Observed.From(new BatteryReading(level, charging), now, StateSource.QueryResponse);
            host.Store.Reset(DeviceState.Empty with
            {
                Device = SampleDevice,
                Profile = new OppoDeviceProfile(OppoModels.EncoX4),
                Connection = DeviceConnectionState.ControlReady,
                ProductId = 0x06EC10,
                ColorId = 1,
                Battery = new BatteryStatus(Level(70), Level(70), Level(100, charging: true)),
            });
            app.CreateViewModelsForDiagnostics(dispatcher);
            await Task.Delay(300);

            // Far outside every monitor, at the real size for this display.
            double scale = ShellEnvironment.GetPrimaryTaskbarMonitor().Scale;
            bool video = app.Device.PopupMediaIsVideo;
            var placement = new PixelRect(-24000, -24000,
                (int)Math.Ceiling((video ? ConnectionPopupWindow.VideoWidth : ConnectionPopupWindow.CardWidth) * scale),
                (int)Math.Ceiling((video ? ConnectionPopupWindow.VideoHeight : ConnectionPopupWindow.CardHeight) * scale));
            result["foregroundAtStart"] = FullscreenDetector.GetState().ToString();
            var popup = app.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(8), clickThrough: false, placement)
                ?? throw new InvalidOperationException("Popup was not created.");
            bool dismissed = false;
            popup.Dismissed += (_, _) => dismissed = true;
            // Set before the first guard tick (250 ms), so a fullscreen app in front cannot switch it
            // to click-through during the interactive check; measured after the fade-in, which starts
            // with the first video frame (up to 600 ms) and takes 420 ms.
            popup.SuspendGuardForDiagnostics = true;
            await Task.Delay(1100);
            result["interactiveReceivesMouse"] = WindowInterop.ReceivesMouseAt(popup, popup.Center);
            popup.SuspendGuardForDiagnostics = false;
            var (windowRect, clientRect) = WindowInterop.GetBounds(popup);
            result["windowRect"] = windowRect.ToString();
            result["clientRect"] = clientRect.ToString();
            result["frameless"] = windowRect == clientRect;
            result["layout"] = popup.Timeline is null ? "card" : "official-video";
            result["timeline"] = popup.Timeline?.ToString();
            result["popupMediaKind"] = video ? "video" : "image";

            await Task.Delay(quick ? 50 : 200);
            if (!quick) await CaptureAsync(popup, Path.Combine(outDir, "t0.8s.png"), result, "capture0");

            popup.ForceClickThroughForDiagnostics();
            await Task.Delay(100);
            result["clickThroughEnabled"] = popup.IsClickThrough;
            result["clickThroughReceivesMouse"] = !dismissed && WindowInterop.ReceivesMouseAt(popup, popup.Center);
            result["dismissedBySafetyNet"] = dismissed;

            if (!dismissed && !quick)
            {
                await Task.Delay(2400);
                await CaptureAsync(popup, Path.Combine(outDir, "t3.2s.png"), result, "capture1");
                await Task.Delay(3800);
                await CaptureAsync(popup, Path.Combine(outDir, "t7.0s.png"), result, "capture2");
                await Task.Delay(1200);
            }
            result["closedOnTimeline"] = dismissed;
            if (!dismissed) popup.DismissNow();

            // A settings change after the pop-up has closed re-applies the theme to every window; it
            // must leave the closed pop-up alone (touching it ended the app).
            app.Windows.Theme.ReapplyForDiagnostics();
            await Task.Delay(300);
            result["settingsChangeAfterClose"] = "ok";
            result["foreground"] = FullscreenDetector.GetState().ToString();
            result["ok"] = (bool?)result["interactiveReceivesMouse"] == true
                && (bool?)result["clickThroughEnabled"] == true
                && (bool?)result["clickThroughReceivesMouse"] == false;
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            result["ok"] = false;
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.TryGetValue("ok", out var ok) && ok is true ? 0 : 1;
    }

    private static async Task CaptureAsync(ConnectionPopupWindow popup, string path, Dictionary<string, object?> result, string key)
    {
        if (WindowInterop.CaptureWindow(popup) is not { } shot)
        {
            result[key] = "failed";
            return;
        }
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, shot.Width, shot.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(shot.Bgra.AsBuffer());
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        result[key] = path;
    }
}
