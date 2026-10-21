// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using EncoDeck.App.Views;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EncoDeck.App.Services;

/// <summary>
/// <c>--popup-preview &lt;video&gt; &lt;out-dir&gt; [--theme light|dark] [--name "OPPO Enco X3"]
/// [--levels 80,75,60] [--at 0.3,1,2.5,4] [--offscreen]</c>: shows the connection pop-up for any
/// pack video (its <c>config.json</c> is found next to or above it) with sample battery levels and
/// no device, captures it at the given seconds and exits.
/// <para>
/// The pop-up is non-activating and never takes focus. It appears where the user's pop-ups appear
/// (captured from the screen as DWM composed it, and with <c>PrintWindow</c>), or entirely
/// off-screen with <c>--offscreen</c> or while a fullscreen app is in front. Like
/// <see cref="PopupSelfTest"/> it runs before the single-instance check, never starts the Bluetooth
/// runtime and never writes the user's settings.
/// </para>
/// </summary>
internal static class PopupPreview
{
    public const string Argument = "--popup-preview";

    public static async Task<int> RunAsync(App app, string[] args, DispatcherQueue dispatcher)
    {
        int at = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
        string video = Path.GetFullPath(args[at + 1]);
        string outDir = at + 2 < args.Length && !args[at + 2].StartsWith("--", StringComparison.Ordinal)
            ? args[at + 2]
            : Path.Combine(Path.GetTempPath(), "EncoDeck-popup-preview");
        string? Value(string flag)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool dark = string.Equals(Value("--theme"), "dark", StringComparison.OrdinalIgnoreCase);
        string name = Value("--name") ?? "OPPO Enco";
        int[] levels = (Value("--levels") ?? "80,75,60").Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        double[] times = (Value("--at") ?? "0.3,1,2.5,4").Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        string prefix = Value("--prefix") ?? $"{Path.GetFileNameWithoutExtension(video)}-{(dark ? "dark" : "light")}";

        Directory.CreateDirectory(outDir);
        var result = new Dictionary<string, object?> { ["video"] = video, ["theme"] = dark ? "dark" : "light" };
        try
        {
            var host = AppHost.Create(Path.Combine(outDir, "logs"));
            app.SetHostForDiagnostics(host);
            var now = DateTimeOffset.UtcNow;
            Observed<BatteryReading>? Level(int level) => level > 0 ? Observed.From(new BatteryReading(level, false), now, StateSource.QueryResponse) : null;
            // Display-only sample state: no device is involved.
            host.Store.Reset(DeviceState.Empty with
            {
                Device = new DeviceIdentity("preview", name, new BluetoothAddress(0)),
                Connection = DeviceConnectionState.ControlReady,
                Battery = new BatteryStatus(Level(levels[0]), Level(levels[1]), Level(levels[2])),
            });
            app.CreateViewModelsForDiagnostics(dispatcher);
            await Task.Delay(300);

            var interruption = FullscreenDetector.GetState();
            bool offscreen = args.Contains("--offscreen", StringComparer.OrdinalIgnoreCase) || interruption != InterruptionState.Available;
            result["foreground"] = interruption.ToString();
            result["offscreen"] = offscreen;
            double scale = ShellEnvironment.GetPrimaryTaskbarMonitor().Scale * host.Settings.Current.PopupScale;
            PixelRect? placement = offscreen
                ? new PixelRect(-24000, -24000, (int)Math.Ceiling(ConnectionPopupWindow.VideoWidth * scale), (int)Math.Ceiling(ConnectionPopupWindow.VideoHeight * scale))
                : null;

            var popup = new ConnectionPopupWindow(TimeSpan.FromSeconds(8), passThrough: false) { MediaOverride = video };
            if (popup.Content is FrameworkElement root) root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            bool dismissed = false;
            popup.Dismissed += (_, _) => dismissed = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            popup.ShowPopup(placement);
            result["timeline"] = popup.Timeline?.ToString();
            result["layout"] = popup.Layout.ToString();

            var shots = new List<object>();
            foreach (double seconds in times)
            {
                var wait = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                if (dismissed) break;
                string tag = seconds.ToString("0.0##", CultureInfo.InvariantCulture);
                var shot = new Dictionary<string, object?> { ["t"] = clock.Elapsed.TotalSeconds, ["state"] = popup.DiagnosticState, ["windowRect"] = WindowInterop.GetBounds(popup).Window.ToString(), ["visibility"] = WindowInterop.DescribeVisibility(popup) };
                if (WindowInterop.CaptureWindow(popup) is { } window)
                {
                    shot["window"] = await SavePngAsync(window, Path.Combine(outDir, $"{prefix}-t{tag}s.png"));
                }
                if (!offscreen && WindowInterop.CaptureScreen(WindowInterop.GetBounds(popup).Window) is { } screen)
                {
                    shot["screen"] = await SavePngAsync(screen, Path.Combine(outDir, $"{prefix}-t{tag}s-screen.png"));
                }
                shots.Add(shot);
            }
            result["shots"] = shots;
            result["closedOnTimeline"] = dismissed;
            if (!dismissed) popup.DismissNow();
            await Task.Delay(200);
            result["ok"] = shots.Count == times.Length;
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            result["ok"] = false;
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, $"{prefix}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.TryGetValue("ok", out var ok) && ok is true ? 0 : 1;
    }

    private static async Task<string> SavePngAsync((int Width, int Height, byte[] Bgra) shot, string path)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, shot.Width, shot.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(shot.Bgra.AsBuffer());
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return Path.GetFullPath(path);
    }
}
