// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using EncoDeck.Core.Privacy;
using EncoDeck.Platform.A2dpBridge;

namespace EncoDeck.Probe;

/// <summary>
/// Connects to the A2DP Windows Bridge core over its pipe, prints its state and the changes for a while. Read-only
/// unless asked: --start launches a core (optionally a side-by-side test instance), --quit ends it afterwards.
/// </summary>
internal static class BridgeCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        string? instance = options.Get("instance");
        bool showAddress = options.Has("show-address");
        if (options.Get("start") is { } folder)
        {
            if (!A2dpBridgeComponent.IsBridgeFolder(folder))
            {
                Console.Error.WriteLine($"No {A2dpBridgeComponent.CoreExe} in {folder}.");
                return 2;
            }
            var environment = new Dictionary<string, string>();
            if (instance is not null) environment["A2DPWB_INSTANCE"] = instance;
            if (options.Get("config-dir") is { } config) environment["A2DPWB_CONFIG_DIR"] = Path.GetFullPath(config);
            A2dpBridgeComponent.StartCore(new BridgeInstallation(Path.GetFullPath(folder), BridgeOrigin.Running, null), environment);
            Console.WriteLine($"Started {A2dpBridgeComponent.CoreExe} --background{(instance is null ? "" : $" (instance {instance})")}.");
        }

        Console.WriteLine($"Core running: {A2dpBridgeMonitor.IsCoreRunning(instance)} (mutex {A2dpBridgeMonitor.MutexName(instance)})");
        if (A2dpBridgeComponent.FindInstalled() is { } installed) Console.WriteLine($"A2DPWB installation: {installed.Directory} ({installed.Version ?? "unknown version"})");
        await using var monitor = new A2dpBridgeMonitor(instance: instance) { IdleInterval = TimeSpan.FromSeconds(1) };
        monitor.Start();
        if (!await monitor.WaitForConnectionAsync(TimeSpan.FromSeconds(options.GetInt("connect-seconds", 15)), ct).ConfigureAwait(false))
        {
            Console.WriteLine($"Not connected: {monitor.Status.Link} {monitor.Status.Detail}");
            return 3;
        }
        Console.WriteLine($"Connected to {monitor.Status.Server}");
        string? windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync().ConfigureAwait(false);
        string last = "";
        void Print()
        {
            if (monitor.Status.Snapshot is not { } s) return;
            string text = Describe(s, showAddress, windowsAdapter);
            if (text == last) return;
            last = text;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
        }
        Print();
        monitor.Changed += (_, _) => Print();

        if (options.Has("diag"))
        {
            var diag = await monitor.RequestAsync("diag.info", new JsonObject { ["mask"] = !showAddress }, ct: ct).ConfigureAwait(false);
            Console.WriteLine(diag?["text"]?.GetValue<string>());
        }
        await Task.Delay(TimeSpan.FromSeconds(options.GetInt("seconds", 5)), ct).ConfigureAwait(false);
        if (options.Has("quit"))
        {
            try { await monitor.RequestAsync("app.quit", ct: ct).ConfigureAwait(false); }
            catch (IOException) { }
            for (int i = 0; i < 50 && A2dpBridgeMonitor.IsCoreRunning(instance); i++) await Task.Delay(100, ct).ConfigureAwait(false);
            Console.WriteLine($"Quit; core running: {A2dpBridgeMonitor.IsCoreRunning(instance)}");
        }
        return 0;
    }

    private static string Describe(BridgeSnapshot s, bool showAddress, string? windowsAdapter)
    {
        string Address(string text) => showAddress || BridgeDeviceMatch.ParseAddress(text) is not { } a ? text : new BluetoothAddress(a).ToRedactedString();
        var lines = new List<string>
        {
            $"adapter {s.AdapterStatus}{(s.AdapterError is { } e ? " (" + A2dpBridgeText.Format(e) + ")" : "")}",
        };
        lines.AddRange(s.Adapters.Select(a => $"  adapter {a.DisplayName} driver={a.Driver}/{a.DriverService} firmware={a.FirmwarePresent?.ToString() ?? "n/a"}"
            + (WindowsAdapterGuard.IsSameDevice(a.InstanceId, windowsAdapter) ? " [Windows' own adapter: never assigned]" : "")));
        lines.AddRange(s.Devices.Select(d => $"  device {Address(d.Address)} '{d.Name}' saved={d.Saved} codecs={string.Join(',', d.Codecs)}"));
        lines.AddRange(s.Profiles.Select(p => $"  profile '{p.Name}' -> {Address(p.DeviceAddress)} {A2dpBridgeText.Codec(p.Codec)}"));
        var info = s.Stream.Info;
        lines.Add($"  stream {s.Stream.State} '{A2dpBridgeText.Format(s.Stream.Message)}' profile={s.Stream.Profile ?? "-"}"
            + (info is null ? "" : $" {info.Codec} {info.BitrateKbps} kbps {info.SampleRate} Hz") + (s.Stream.Volume is { } v ? $" volume={v}" : ""));
        return string.Join(Environment.NewLine, lines);
    }
}
