// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.App.Views;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EncoDeck.App.Services;

/// <summary>
/// <c>--ui-snapshot &lt;dir&gt;</c>: renders every page of the main window <b>off-screen</b> (never on
/// a monitor, never activated) with a sample Enco X4 state and captures only that window, scrolling
/// through long pages. Like <see cref="PopupSelfTest"/> it runs before the single-instance check and
/// never starts the Bluetooth runtime.
/// </summary>
internal static class UiSnapshot
{
    public const string Argument = "--ui-snapshot";

    private static readonly string[] Pages = ["Home", "Sound", "Controls", "Devices", "Settings"];

    public static async Task<int> RunAsync(App app, string outDir, DispatcherQueue dispatcher, string? modelFile = null)
    {
        Directory.CreateDirectory(outDir);
        var host = AppHost.Create(Path.Combine(outDir, "logs"));
        app.SetHostForDiagnostics(host);
        host.Store.Reset(SampleState(DateTimeOffset.UtcNow));
        app.CreateViewModelsForDiagnostics(dispatcher);
        await Task.Delay(300);
        // Display only: a test turntable can be given with --model <file>.
        if (modelFile is not null && File.Exists(modelFile)) app.Device.DeviceModelPath = modelFile;

        var main = new MainWindow();
        double scale = WindowInterop.GetScale(main);
        var appWindow = WindowInterop.GetAppWindow(main);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1160 * scale), (int)(760 * scale)));
        appWindow.Show(activateWindow: false);

        var errors = new List<string>();
        foreach (string page in Pages)
        {
            try
            {
                main.Navigate(page);
                await Task.Delay(900);
                foreach (var view in Descendants(main.Content).OfType<Controls.DeviceModelView>()) errors.Add($"model {page}: {view.DiagnosticState}");
                foreach (var sw in Descendants(main.Content).OfType<ToggleSwitch>())
                {
                    errors.Add($"layout {page}: ToggleSwitch ActualWidth={sw.ActualWidth:0.#} MinWidth={sw.MinWidth:0.#} Width={sw.Width:0.#} Margin={sw.Margin} Align={sw.HorizontalAlignment} Parent={VisualTreeHelper.GetParent(sw)?.GetType().Name}");
                }
                var scroller = FindScrollViewer(main.Content);
                int shot = 0;
                double offset = 0;
                while (true)
                {
                    await CaptureAsync(main, Path.Combine(outDir, $"{page}-{shot++}.png"));
                    if (scroller is null || offset >= scroller.ScrollableHeight || shot >= 6) break;
                    offset = Math.Min(scroller.ScrollableHeight, offset + scroller.ViewportHeight * 0.85);
                    scroller.ChangeView(null, offset, null, disableAnimation: true);
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{page}: {ex}");
            }
        }
        main.Close();

        try
        {
            // Wide window with the navigation pane collapsed (the layout users get from the
            // hamburger button), still off-screen and not activated.
            var wide = new MainWindow();
            WindowInterop.GetAppWindow(wide).MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1148 * scale), (int)(754 * scale)));
            WindowInterop.GetAppWindow(wide).Show(activateWindow: false);
            await Task.Delay(600);
            wide.SetPaneOpenForDiagnostics(false);
            foreach (string page in new[] { "Home", "Sound" })
            {
                wide.Navigate(page);
                await Task.Delay(900);
                await CaptureAsync(wide, Path.Combine(outDir, $"Collapsed-{page}-0.png"));
                for (DependencyObject? e = FindScrollViewer(wide.Content); e is not null; e = VisualTreeHelper.GetParent(e))
                {
                    if (e is FrameworkElement fe) errors.Add($"collapsed {page}: {fe.GetType().Name} {fe.Name} width={fe.ActualWidth:0.#} desired={fe.DesiredSize.Width:0.#} offset={fe.ActualOffset.X:0.#}");
                }
                if (FindScrollViewer(wide.Content) is { } sv) errors.Add($"collapsed {page}: viewport={sv.ViewportWidth:0.#} extent={sv.ExtentWidth:0.#} content={(sv.Content as FrameworkElement)?.ActualWidth:0.#}");
            }
            wide.Close();
        }
        catch (Exception ex)
        {
            errors.Add($"Collapsed: {ex}");
        }

        try
        {
            // Tray quick panel, also off-screen and not activated.
            var panel = new TrayPanelWindow();
            new ThemeService(host.Settings).Register(panel);
            panel.ShowForDiagnostics(new Platform.Shell.PixelPoint(-24000, -22000), scale);
            await Task.Delay(900);
            await CaptureAsync(panel, Path.Combine(outDir, "TrayPanel-0.png"));
            panel.Close();
        }
        catch (Exception ex)
        {
            errors.Add($"TrayPanel: {ex}");
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "errors.txt"), string.Join("\n\n", errors));
        return errors.Count == 0 ? 0 : 1;
    }

    /// <summary>Values like the ones read from a real X4 (display only, never shown to the user).</summary>
    internal static DeviceState SampleState(DateTimeOffset now)
    {
        Observed<T> O<T>(T value) => Observed.From(value, now, StateSource.QueryResponse);
        Observed<BatteryReading> Level(int level, bool charging = false) => O(new BatteryReading(level, charging));
        var toggles = new Dictionary<DeviceToggle, Observed<bool>>
        {
            [DeviceToggle.GamingMode] = O(false),
            [DeviceToggle.LowLatency] = O(true),
            [DeviceToggle.InEarDetection] = O(true),
            [DeviceToggle.DualDeviceConnection] = O(true),
            [DeviceToggle.SleepDetection] = O(true),
            [DeviceToggle.SwiftPair] = O(true),
            [DeviceToggle.VoiceCommands] = O(false),
            [DeviceToggle.HeadGestures] = O(true),
            [DeviceToggle.BassEngine] = O(true),
            [DeviceToggle.HiRes] = O(false),
        };
        var keys = new List<KeyFunction>();
        foreach (var side in new[] { EarSide.Left, EarSide.Right })
        {
            keys.Add(new KeyFunction(side, 1, GestureAction.SingleSqueeze, 1));
            keys.Add(new KeyFunction(side, 1, GestureAction.DoubleSqueeze, 6));
            keys.Add(new KeyFunction(side, 1, GestureAction.TripleSqueeze, 5));
            keys.Add(new KeyFunction(side, 1, GestureAction.SqueezeAndHold, 8));
            keys.Add(new KeyFunction(side, 1, GestureAction.Slide, 7));
            keys.Add(new KeyFunction(side, KeyFunction.CallButton, GestureAction.SingleSqueeze, 29));
            keys.Add(new KeyFunction(side, KeyFunction.CallButton, GestureAction.DoubleSqueeze, 28));
        }
        int[] bands = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        return DeviceState.Empty with
        {
            Device = new DeviceIdentity("snapshot", "OPPO Enco X4", new BluetoothAddress(0)),
            Profile = new OppoDeviceProfile(OppoModels.EncoX4),
            Connection = DeviceConnectionState.ControlReady,
            ProductId = 0x06EC10,
            ColorId = 1,
            Battery = new BatteryStatus(Level(70), Level(70), Level(100, charging: true)),
            Wear = O(new WearStatus(true, true, false, false, false)),
            NoiseControl = O(new NoiseControlState(NoiseControlMode.NoiseCancellation, "smart")),
            SmartLevelId = "medium",
            Equalizer = O(new EqualizerState("dynaudio-original", 0)),
            Firmware = O(new FirmwareInfo("116", "06EC10", new Dictionary<string, string> { ["left"] = "116", ["right"] = "116", ["case"] = "100" })),
            Toggles = toggles.ToImmutableDictionary(),
            Spatial = O(SpatialAudioMode.Off),
            GameSound = O(new GameSoundState(3, new[] { 3, 0 })),
            PromptVolume = O(6),
            BassEngine = O(new BassEngineState(-5, 5, 0)),
            KeyFunctions = O<IReadOnlyList<KeyFunction>>(keys),
            NoiseCycle = O(new NoiseCycleState(0x0006, 0x0006)),
            HeadMotionType = O(0),
            SourceDevices = O<IReadOnlyList<SourceDevice>>(new SourceDevice[]
            {
                new([0x11, 0x22, 0x33, 0x44, 0x55, 0x66], "Laptop", 2, true, true, 2),
                new([0x01, 0x02, 0x03, 0x04, 0x05, 0x06], "Phone with a long name", 2, false, false, 1),
                new([0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F], "Watch", 0, false, false, 4),
            }),
            PreferredDevice = O(new PreferredDeviceState(null)),
            CustomEqualizers = O<IReadOnlyList<CustomEqualizer>>(new CustomEqualizer[]
            {
                new(4, "自定义1", -6, 6, false, bands.Select(f => new EqualizerBand(f, 0)).ToArray()),
            }),
        };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        // The page's own ScrollViewer: the largest one inside the frame content.
        ScrollViewer? best = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is ScrollViewer sv && sv.ScrollableHeight > (best?.ScrollableHeight ?? -1) && sv.ActualHeight > 200) best = sv;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return best;
    }

    private static async Task CaptureAsync(Window window, string path)
    {
        if (WindowInterop.CaptureWindow(window) is not { } shot) return;
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, shot.Width, shot.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(shot.Bgra.AsBuffer());
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }
}
