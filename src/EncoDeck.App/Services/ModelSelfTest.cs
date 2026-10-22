// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using EncoDeck.App.Controls;
using Microsoft.UI.Xaml;

namespace EncoDeck.App.Services;

/// <summary>Runs model rotation and cache eviction off-screen without Bluetooth or single-instance activation.</summary>
internal static class ModelSelfTest
{
    public const string Argument = "--model-selftest";
    public const string PreviewArgument = "--model-preview";

    /// <summary>
    /// <c>--model-preview &lt;model&gt; &lt;productId hex&gt; &lt;colorId&gt; &lt;name&gt;</c>: the main window's home page
    /// with sample state for that model, for manual testing; no Bluetooth runtime, settings untouched.
    /// </summary>
    public static async Task<Window> ShowPreviewAsync(App app, string[] args, int at, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        string modelPath = Path.GetFullPath(args[at + 1]);
        uint productId = Convert.ToUInt32(args[at + 2], 16);
        var host = AppHost.Create(Path.Combine(Path.GetTempPath(), "EncoDeck-model-preview", "logs"));
        app.SetHostForDiagnostics(host);
        var model = Protocol.Oppo.Models.OppoModels.FindByProductId(productId) ?? Protocol.Oppo.Models.OppoModels.EncoX4;
        host.Store.Reset(UiSnapshot.SampleState(DateTimeOffset.UtcNow) with
        {
            Device = new Core.Devices.DeviceIdentity("preview", args[at + 4], new Core.Privacy.BluetoothAddress(0)),
            Profile = new Protocol.Oppo.Models.OppoDeviceProfile(model),
            ProductId = productId,
            ColorId = int.Parse(args[at + 3], System.Globalization.CultureInfo.InvariantCulture),
        });
        app.CreateViewModelsForDiagnostics(dispatcher);
        await Task.Delay(300);
        app.Device.DeviceModelPath = modelPath;
        var main = new Views.MainWindow { Title = "EncoDeck preview - " + args[at + 4] };
        main.Activate();
        main.Navigate("Home");
        return main;
    }

    public static async Task RunAsync(string output, string modelPath)
    {
        Directory.CreateDirectory(output);
        var result = new Dictionary<string, object?>();
        Window? window = null;
        try
        {
            var view = new DeviceModelView { Width = 520, Height = 400, ModelPath = modelPath };
            window = new Window { Content = view };
            WindowInterop.GetAppWindow(window).MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, 600, 500));
            WindowInterop.GetAppWindow(window).Show(activateWindow: false);
            if (Platform.Media.Scene3D.VfxScene.IsSceneFile(modelPath))
            {
                var log = new List<string>();
                await view.ExerciseSceneForDiagnosticsAsync(output, line => log.Add(line));
                result["steps"] = log;
                result["state"] = view.DiagnosticState;
                // Unload and reload: everything is released and the scene comes back.
                window.Content = null;
                await Task.Delay(300);
                window.Content = view;
                string again = Directory.CreateDirectory(Path.Combine(output, "reload")).FullName;
                await view.ExerciseSceneForDiagnosticsAsync(again, line => log.Add("reload " + line));
                window.Content = null;
                await Task.Delay(200);
                result["ok"] = true;
                return;
            }
            await view.ExerciseRotationForDiagnosticsAsync(phase => File.WriteAllText(Path.Combine(output, "phase.txt"), phase));
            result["state"] = view.DiagnosticState;
            window.Content = null; // Unload while XAML may still hold composition work for old frames.
            await Task.Delay(200);
            window.Content = view;
            await view.ExerciseRotationForDiagnosticsAsync(phase => File.WriteAllText(Path.Combine(output, "phase.txt"), "reload: " + phase));
            window.Content = null;
            await Task.Delay(200);
            result["ok"] = true;
        }
        catch (Exception ex) { result["ok"] = false; result["error"] = ex.ToString(); }
        finally
        {
            // Written before the window closes: closing the last window may end the process.
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            window?.Close();
        }
    }
}
