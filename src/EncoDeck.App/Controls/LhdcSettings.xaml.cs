// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Updates;
using EncoDeck.Platform.Packaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

public sealed partial class LhdcSettings : UserControl
{
    private sealed record Choice(int Value, string Label);
    private CancellationTokenSource? _download;
    private bool _busy, _syncing;

    public LhdcSettings()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Cancel.Content = Loc.Get("Lhdc_Cancel");
            CheckUpdates.Content = Loc.Get("Component_CheckUpdates");
            Uninstall.Content = Loc.Get("Component_Uninstall");
            Devices.Header = Loc.Get("Lhdc_Device");
            Rate.Header = Loc.Get("Lhdc_Rate");
            Depth.Header = Loc.Get("Lhdc_Depth");
            Bitrate.Header = Loc.Get("Lhdc_Bitrate");
            Adaptive.Content = Loc.Get("Lhdc_Adaptive");
            Apply.Content = Loc.Get("Lhdc_Apply");
            Restore.Content = Loc.Get("Lhdc_Restore");
            Refresh.Content = Loc.Get("Lhdc_Refresh");
            Rate.ItemsSource = new[] { new Choice(44100, "44.1 kHz"), new Choice(48000, "48 kHz"), new Choice(96000, "96 kHz"), new Choice(192000, "192 kHz") };
            Rate.SelectedIndex = 1;
            Depth.ItemsSource = new[] { new Choice(16, "16 bit"), new Choice(24, "24 bit") };
            Depth.SelectedIndex = 1;
            Bitrate.ItemsSource = new[] { 64, 160, 192, 256, 320, 400, 500, 900, 1000 }.Select(k => new Choice(k, $"{k} kbps")).ToArray();
            Bitrate.SelectedIndex = 5;
            Status.Text = StatusText();
            Sync();
            if (LhdcCompanion.IsReady) await Operate(RefreshDevices);
            // At most once a day, quietly: a later component release can arrive without an app update.
            if (await LhdcCompanion.CheckForUpdateAsync(App.Instance.Host.Settings.Current.IncludePrereleaseUpdates, force: false) is not null && !_busy)
            {
                Status.Text = StatusText();
                Sync();
            }
        };
        Unloaded += (_, _) => _download?.Cancel();
    }

    /// <summary>The state, and which component build is installed (an app update keeps using it until a newer pin is downloaded).</summary>
    private static string StatusText()
    {
        string text = Loc.Get(LhdcCompanion.StatusKey);
        if (LhdcCompanion.Component.Installed is { } installed)
            text += Environment.NewLine + Loc.Format("Lhdc_Version", installed.Version);
        if (LhdcCompanion.UpdateTarget is { } update) text += Environment.NewLine + Loc.Format("Lhdc_UpdateAvailable", update.Version);
        return text;
    }

    private void Sync()
    {
        Download.Content = LhdcCompanion.UpdateAvailable ? Loc.Format("Lhdc_Update", LhdcCompanion.PackageMegabytes)
            : Loc.Format("Lhdc_DownloadSize", LhdcCompanion.PackageMegabytes);
        Editor.Visibility = LhdcCompanion.IsReady ? Visibility.Visible : Visibility.Collapsed;
        Download.Visibility = LhdcCompanion.CanDownload || PackageIdentity.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
        Download.IsEnabled = !_busy && LhdcCompanion.CanDownload;
        if (PackageIdentity.IsPackaged) Download.Content = Loc.Get("Lhdc_StoreDisabled");
        StandaloneLink.Visibility = PackageIdentity.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
        bool manage = !PackageIdentity.IsPackaged && LhdcCompanion.StatusKey is "Lhdc_Ready" or "Lhdc_Missing" or "Lhdc_Unpublished";
        CheckUpdates.Visibility = manage && _download is null ? Visibility.Visible : Visibility.Collapsed;
        Uninstall.Visibility = manage && LhdcCompanion.IsDownloaded && _download is null ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdates.IsEnabled = Uninstall.IsEnabled = !_busy;
        Devices.IsEnabled = Rate.IsEnabled = Depth.IsEnabled = Bitrate.IsEnabled = Adaptive.IsEnabled = Refresh.IsEnabled = !_busy;
        Apply.IsEnabled = Restore.IsEnabled = !_busy && Devices.SelectedItem is LhdcDevice;
    }

    private async Task Operate(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Sync();
        try { await action(); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; Sync(); }
    }

    private async Task RefreshDevices()
    {
        var backend = LhdcCompanion.Backend;
        var targets = await backend.QueryAsync(null, "targets");
        string? selected = (Devices.SelectedItem as LhdcDevice)?.Instance ?? targets["selected_instance"]?.GetValue<string>();
        var devices = targets["devices"]!.AsArray().Select(d => new LhdcDevice(d!["instance"]!.GetValue<string>(), d["name"]!.GetValue<string>(), d["address"]!.GetValue<string>())).ToArray();
        _syncing = true;
        try
        {
            Devices.ItemsSource = devices;
            Devices.SelectedItem = devices.FirstOrDefault(d => d.Instance.Equals(selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncing = false; }
        await ReadStatus();
    }

    private async Task ReadStatus()
    {
        Observed.Text = "";
        if (Devices.SelectedItem is not LhdcDevice device) { Observed.Text = Loc.Get("Lhdc_SelectDevice"); return; }
        var backend = LhdcCompanion.Backend;
        var inventory = await backend.QueryAsync(device.Instance, "inspect");
        var audio = await backend.QueryAsync(device.Instance, "audio-status");
        var target = inventory["devices"]!.AsArray().FirstOrDefault(d => string.Equals(d?["instance_id"]?.GetValue<string>(), device.Instance, StringComparison.OrdinalIgnoreCase));
        string service = target?["service"]?.GetValue<string>() ?? "";
        string mode = service == "BthA2dp" ? Loc.Get("Lhdc_WindowsMode")
            : service == "lhdc-transport" && audio["running"]?.GetValue<bool>() == true ? "LHDC V5" : Loc.Get("Lhdc_UnknownMode");
        int active = audio["active_kbps"]?.GetValue<int>() ?? 0;
        Observed.Text = Loc.Format("Lhdc_Observed", mode) + (active > 0 ? $" · {active} kbps" : "");
    }

    private async void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) await Operate(ReadStatus);
        Sync();
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Operate(RefreshDevices);
    private async void OnApply(object sender, RoutedEventArgs e) => await ChangeMode(true);
    private async void OnRestore(object sender, RoutedEventArgs e) => await ChangeMode(false);

    private async Task ChangeMode(bool lhdc) => await Operate(async () =>
    {
        if (Devices.SelectedItem is not LhdcDevice device) return;
        int rate = (Rate.SelectedItem as Choice)?.Value ?? 48000;
        int kbps = (Bitrate.SelectedItem as Choice)?.Value ?? 400;
        if (rate == 44100) kbps = kbps switch { 256 => 240, 500 => 480, _ => kbps };
        var profile = new LhdcProfile(rate, (Depth.SelectedItem as Choice)?.Value ?? 24, kbps, Adaptive.IsChecked == true);
        Status.Text = Loc.Get("Lhdc_Applying");
        var backend = LhdcCompanion.Backend;
        // Local format validation first; the deployment probes the selected earbud's actual capabilities.
        if (lhdc) await backend.QueryAsync(device.Instance, "profile-check", $"{profile.SampleRate}", $"{profile.Bits}", $"{profile.Kbps}");
        await backend.ApplyAsync(device.Instance, lhdc, profile);
        Status.Text = Loc.Get("Lhdc_Applied");
        await ReadStatus();
    });

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        Status.Text = Loc.Get("Component_Checking");
        var outcome = await LhdcCompanion.CheckForUpdateAsync(App.Instance.Host.Settings.Current.IncludePrereleaseUpdates, force: true);
        Status.Text = StatusText() + Environment.NewLine + outcome switch
        {
            ComponentUpdateOutcome.Available => Loc.Format("Component_UpdateFound", LhdcCompanion.UpdateTarget?.Version ?? LhdcCompanion.OnlineRelease?.Version),
            ComponentUpdateOutcome.UpToDate => Loc.Get("Component_UpToDate"),
            ComponentUpdateOutcome.RateLimited => Loc.Get("Component_RateLimited"),
            ComponentUpdateOutcome.Offline => Loc.Get("Component_Offline"),
            _ => Loc.Get("Component_CheckFailed"),
        };
    });

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("Lhdc_UninstallTitle"),
            Content = new TextBlock { Text = Loc.Get("Lhdc_UninstallBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("Component_Uninstall"),
            CloseButtonText = Loc.Get("Lhdc_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Operate(async () =>
        {
            await LhdcCompanion.UninstallAsync();
            Devices.ItemsSource = null;
            Observed.Text = "";
            Status.Text = StatusText() + Environment.NewLine + Loc.Get("Lhdc_Uninstalled");
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _download?.Cancel();
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (!LhdcCompanion.CanDownload) return;
        await Operate(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        Progress.Visibility = Cancel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Status.Text = Loc.Get("Lhdc_Downloading");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (_download != cancellation) return;
                Progress.IsIndeterminate = false;
                Progress.Value = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
                Status.Text = Loc.Format("Lhdc_DownloadProgress", p.Received / 1048576.0, p.Total / 1048576.0);
            });
            await LhdcCompanion.DownloadAsync(progress, cancellation.Token);
            Status.Text = StatusText();
            await RefreshDevices();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Status.Text = Loc.Get("Lhdc_Cancelled"); }
        finally { _download = null; Progress.Visibility = Cancel.Visibility = Visibility.Collapsed; }
        });
    }
}
