// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using EncoDeck.App.Services;
using EncoDeck.Platform.A2dpBridge;
using EncoDeck.Platform.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: A2DP Windows Bridge on a dedicated USB adapter (LDAC, LHDC V5, aptX HD… without test signing).
/// EncoDeck downloads the pinned component, starts and quits the core and covers daily use; first-run pairing and
/// firmware stay in A2DPWB's own window. See docs/A2DP_BRIDGE.md.
/// </summary>
public sealed partial class A2dpBridgeSettings : UserControl
{
    private sealed record ProfileChoice(string Name, string Label);

    private sealed record Candidate(string Address, string Name, string Label);

    private sealed record CodecChoice(string Key, string Label);

    /// <summary>A2DPWB's codec keys for a new profile; Auto picks the best one the earbuds offer.</summary>
    private static readonly string[] Codecs = ["auto", "ldac", "lhdcv5", "aptxhd", "aptx", "aac", "sbcxq", "sbc"];

    private CancellationTokenSource? _download;
    private DispatcherQueueTimer? _volumeTimer;
    private bool _busy, _syncing, _refreshQueued;
    private string? _windowsAdapter;
    private string _profileSignature = "";
    private string _candidateSignature = "";
    private string? _selectProfile;

    public A2dpBridgeSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Cancel.Content = Loc.Get("Common_Cancel");
            StartCore.Content = Loc.Get("Bridge_Start");
            QuitCore.Content = Loc.Get("Bridge_Quit");
            OpenWindow.Content = Loc.Get("Bridge_OpenWindow");
            ConnectButton.Content = Loc.Get("Bridge_Connect");
            DisconnectButton.Content = Loc.Get("Bridge_Disconnect");
            Profiles.Header = Loc.Get("Bridge_Profile");
            Volume.Header = Loc.Get("Bridge_Volume");
            RefreshAdapters.Content = Loc.Get("Bridge_RefreshAdapters");
            OpenSettingsFolder.Content = Loc.Get("Bridge_OpenSettingsFolder");
            Candidates.Header = Loc.Get("Bridge_Candidate");
            Scan.Content = Loc.Get("Bridge_Scan");
            Codec.Header = Loc.Get("Bridge_Codec");
            SaveProfile.Content = Loc.Get("Bridge_SaveProfile");
            Codec.ItemsSource = Codecs.Select(c => new CodecChoice(c, A2dpBridgeText.Codec(c))).ToArray();
            Codec.SelectedIndex = 0;
            Bridge.Changed += OnBridgeChanged;
            Bridge.RefreshInstallations();
            Bridge.Monitor.Wake();
            _ = LoadWindowsAdapterAsync();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Bridge.Changed -= OnBridgeChanged;
            _download?.Cancel();
            _volumeTimer?.Stop();
        };
    }

    private static A2dpBridgeCompanion Bridge => App.Instance.Host.Bridge;

    private void OnBridgeChanged(object? sender, EventArgs e)
    {
        // Coalesce bursts of core events into one refresh.
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private async Task LoadWindowsAdapterAsync()
    {
        _windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync();
        Refresh();
    }

    private void Refresh()
    {
        var status = Bridge.Status;
        var installation = Bridge.Installation;
        var component = Bridge.Component;
        bool manage = A2dpBridgeCompanion.CanManage;
        bool connected = status.IsConnected;
        bool managedUpdate = Bridge.ManagedUpdateAvailable;
        double megabytes = (component.Package?.Size ?? 0) / 1048576.0;

        string text = status.Link switch
        {
            BridgeLink.Connected => Loc.Format("Bridge_Running", status.ServerVersion ?? "?", Origin(installation?.Origin)),
            BridgeLink.Incompatible => Loc.Format("Bridge_Incompatible", status.Detail),
            BridgeLink.Connecting => Loc.Get("Bridge_Connecting"),
            _ when installation is not null => Loc.Format("Bridge_Stopped", installation.Version ?? "?", Origin(installation.Origin)),
            _ when !manage => Loc.Get("Bridge_StoreMissing"),
            _ => component.CanInstall ? Loc.Format("Bridge_Missing", megabytes) : Loc.Get("Bridge_Unpublished"),
        };
        if (managedUpdate) text += Environment.NewLine + Loc.Format("Bridge_UpdateAvailable", component.Release.Version);
        if (!manage && installation is not null && !connected) text += Environment.NewLine + Loc.Get("Bridge_StoreStart");
        // An operation in progress keeps its own status line until it ends.
        if (_download is null && !_busy) Status.Text = text;

        bool downloading = _download is not null;
        StandaloneLink.Visibility = Show(!manage && installation is null);
        // A2DPWB's own installation updates itself; EncoDeck's copy is only offered without one.
        Download.Visibility = Show(manage && component.CanInstall && (installation is null || managedUpdate) && !downloading);
        Download.Content = managedUpdate ? Loc.Format("Bridge_Update", megabytes) : Loc.Format("Bridge_Download", megabytes);
        Download.IsEnabled = !_busy;
        StartCore.Visibility = Show(manage && installation is not null && status.Link == BridgeLink.NotRunning);
        QuitCore.Visibility = Show(connected);
        OpenWindow.Visibility = Show(manage && installation is not null);
        StartCore.IsEnabled = QuitCore.IsEnabled = OpenWindow.IsEnabled = !_busy;
        AutoStart.Visibility = Show(manage && installation is not null);
        _syncing = true;
        try { AutoStart.IsOn = App.Instance.Host.Settings.Current.A2dpBridgeAutoStart; }
        finally { _syncing = false; }

        Live.Visibility = AdaptersPanel.Visibility = Show(connected);
        if (connected) ShowLive(status.Snapshot!);
    }

    private void ShowLive(BridgeSnapshot snapshot)
    {
        AdapterText.Text = DescribeAdapters(snapshot);
        StreamText.Text = DescribeStream(snapshot);

        var choices = snapshot.Profiles.Select(p => new ProfileChoice(p.Name, p.Name == p.DeviceName
            ? $"{p.Name} · {A2dpBridgeText.Codec(p.Codec)}"
            : Loc.Format("Bridge_ProfileLabel", p.Name, p.DeviceName.Length > 0 ? p.DeviceName : p.DeviceAddress, A2dpBridgeText.Codec(p.Codec)))).ToArray();
        string signature = string.Join('\n', choices.Select(c => c.Label));
        // A profile just saved here is selected once it arrives; otherwise the streamed one, the chosen one, the core's active one.
        string? wanted = _selectProfile is { } saved && choices.Any(c => c.Name == saved) ? saved
            : snapshot.ActiveStreamProfile?.Name ?? (Profiles.SelectedItem as ProfileChoice)?.Name ?? snapshot.ActiveProfile;
        _syncing = true;
        try
        {
            if (signature != _profileSignature)
            {
                _profileSignature = signature;
                Profiles.ItemsSource = choices;
            }
            if (snapshot.ActiveStreamProfile is not null || Profiles.SelectedItem is null || wanted == _selectProfile)
                Profiles.SelectedItem = choices.FirstOrDefault(c => c.Name == wanted) ?? choices.FirstOrDefault();
            if (wanted == _selectProfile) _selectProfile = null;
        }
        finally { _syncing = false; }
        ProfileRow.Visibility = Show(choices.Length > 0);
        NoProfiles.Visibility = Show(choices.Length == 0);
        string? selected = (Profiles.SelectedItem as ProfileChoice)?.Name;
        bool active = snapshot.Stream.IsActive;
        ConnectButton.IsEnabled = !_busy && selected is not null && !(active && snapshot.Stream.Profile == selected) && snapshot.HasUsableAdapter;
        DisconnectButton.IsEnabled = !_busy && active;
        Profiles.IsEnabled = !_busy;

        bool volume = snapshot.Stream.State == BridgeStreamState.Streaming && snapshot.Stream.Volume is not null;
        Volume.Visibility = Show(volume);
        if (volume && _volumeTimer?.IsRunning != true)
        {
            _syncing = true;
            try { Volume.Value = snapshot.Stream.Volume!.Value; }
            finally { _syncing = false; }
        }
        ShowAdapters(snapshot);
        ShowCandidates(snapshot);
    }

    /// <summary>EncoDeck's current earbuds first (their address is known from Windows), then what A2DPWB found or saved.</summary>
    private void ShowCandidates(BridgeSnapshot snapshot)
    {
        var list = new List<Candidate>();
        if (App.Instance.Host.Store.Current.Device is { Transport: EncoDeck.Core.Devices.DeviceTransport.Bluetooth } current && current.Address.Value != 0)
            list.Add(new Candidate(current.Address.ToFullString(), current.Name, Loc.Format("Bridge_CandidateCurrent", current.Name)));
        foreach (var device in snapshot.Devices.Where(d => d.Audio && !d.LeAudio))
        {
            if (BridgeDeviceMatch.ParseAddress(device.Address) is not { } address || list.Any(c => BridgeDeviceMatch.ParseAddress(c.Address) == address)) continue;
            string name = device.Name.Length > 0 ? device.Name : Loc.Get("Bridge_UnknownDevice");
            string where = new EncoDeck.Core.Privacy.BluetoothAddress(address).ToRedactedString();
            list.Add(new Candidate(device.Address, device.Name, device.Rssi is { } rssi ? $"{name} · {where} · {rssi} dBm" : $"{name} · {where}"));
        }
        string signature = string.Join('\n', list.Select(c => c.Label));
        if (signature != _candidateSignature)
        {
            _candidateSignature = signature;
            string? kept = (Candidates.SelectedItem as Candidate)?.Address;
            Candidates.ItemsSource = list;
            Candidates.SelectedItem = list.FirstOrDefault(c => c.Address == kept) ?? list.FirstOrDefault();
        }
        NoCandidates.Visibility = Show(list.Count == 0);
        Scan.Content = Loc.Get(snapshot.Scanning ? "Bridge_Scanning" : "Bridge_Scan");
        Scan.IsEnabled = !_busy && !snapshot.Scanning && snapshot.HasUsableAdapter;
        SaveProfile.IsEnabled = !_busy && Candidates.SelectedItem is Candidate;
        Candidates.IsEnabled = Codec.IsEnabled = !_busy;
    }

    private void OnCandidateChanged(object sender, SelectionChangedEventArgs e) =>
        SaveProfile.IsEnabled = !_busy && Candidates.SelectedItem is Candidate;

    private async void OnScan(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        Status.Text = Loc.Get("Bridge_ScanningLong");
        await Bridge.ScanAsync();
    });

    private async void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        if (Candidates.SelectedItem is not Candidate candidate) return;
        string codec = (Codec.SelectedItem as CodecChoice)?.Key ?? "auto";
        await Operate(async () =>
        {
            string name = await Bridge.SaveProfileAsync(candidate.Name, candidate.Address, codec);
            _selectProfile = name;
            AddPanel.IsExpanded = false;
            ShowInfo(Loc.Format("Bridge_ProfileSaved", name));
        });
    }

    private static string DescribeAdapters(BridgeSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.V1Running) lines.Add(Loc.Get("Bridge_V1Running"));
        var usable = snapshot.Adapters.Where(a => a.Driver == BridgeDriver.WinUsb).ToArray();
        if (usable.Length == 0) lines.Add(Loc.Get("Bridge_NoAdapter"));
        foreach (var adapter in usable)
        {
            lines.Add(Loc.Format("Bridge_AdapterLine", adapter.DisplayName, Loc.Get($"Bridge_AdapterStatus_{snapshot.AdapterStatus}")));
            if (adapter.FirmwareInvalid is { } invalid) lines.Add(Loc.Format("Bridge_FirmwareInvalid", invalid));
            else if (adapter.FirmwarePresent == false) lines.Add(Loc.Format("Bridge_FirmwareMissing", string.Join(", ", adapter.FirmwareFiles)));
            if (adapter.VendorNote is { } note) lines.Add(A2dpBridgeText.Format(note));
        }
        if (snapshot.AdapterError is { } error) lines.Add(A2dpBridgeText.Format(error));
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeStream(BridgeSnapshot snapshot)
    {
        var stream = snapshot.Stream;
        string state = stream.Message.IsEmpty ? Loc.Get($"Bridge_Stream_{stream.State}") : A2dpBridgeText.Format(stream.Message);
        if (stream.State != BridgeStreamState.Streaming || stream.Info is not { } info) return Loc.Format("Bridge_StreamLine", state);
        var parts = new List<string> { A2dpBridgeText.Codec(info.Codec) };
        if (info.BitrateKbps > 0) parts.Add(info.Abr ? Loc.Format("Bridge_BitrateAbr", info.BitrateKbps) : $"{info.BitrateKbps} kbps");
        if (info.SampleRate > 0) parts.Add((info.SampleRate / 1000.0).ToString(info.SampleRate % 1000 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " kHz");
        if (info.LeSummary is { Length: > 0 } le) parts.Add(le);
        string source = info.SourceKind switch
        {
            "virtual" => Loc.Format("Bridge_SourceVirtual", info.SourceName),
            "app" => Loc.Format(info.SourceWaiting ? "Bridge_SourceAppWaiting" : "Bridge_SourceApp", info.SourceName),
            "tone" => Loc.Get("Bridge_SourceTone"),
            _ => Loc.Get("Bridge_SourceLoopback"),
        };
        string line = Loc.Format("Bridge_StreamingLine", state, string.Join(" · ", parts), source);
        if (info.RequestedCodec is { } requested && !info.RequestedInUse) line += Environment.NewLine + Loc.Format("Bridge_CodecFallback", A2dpBridgeText.Codec(requested));
        return line;
    }

    private void ShowAdapters(BridgeSnapshot snapshot)
    {
        AdapterList.Children.Clear();
        if (snapshot.Adapters.Count == 0)
        {
            AdapterList.Children.Add(new TextBlock { Text = Loc.Get("Bridge_NoUsbAdapters"), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
            return;
        }
        foreach (var adapter in snapshot.Adapters)
        {
            bool isWindows = WindowsAdapterGuard.IsSameDevice(adapter.InstanceId, _windowsAdapter);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string driver = adapter.Driver switch
            {
                BridgeDriver.WinUsb => Loc.Get("Bridge_DriverWinUsb"),
                BridgeDriver.Bluetooth => Loc.Get(isWindows ? "Bridge_DriverWindowsInUse" : "Bridge_DriverBluetooth"),
                BridgeDriver.None => Loc.Get("Bridge_DriverNone"),
                _ => Loc.Format("Bridge_DriverOther", adapter.DriverService),
            };
            var label = new TextBlock
            {
                Text = $"{adapter.DisplayName}{Environment.NewLine}{driver}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(label);
            var button = new Button
            {
                Content = Loc.Get(adapter.Driver == BridgeDriver.WinUsb ? "Bridge_RestoreDriver" : "Bridge_AssignWinUsb"),
                // Never offer Windows' own adapter: the earbuds' control, and every other paired device, run on it.
                IsEnabled = !_busy && !(adapter.Driver != BridgeDriver.WinUsb && isWindows),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isWindows && adapter.Driver != BridgeDriver.WinUsb) ToolTipService.SetToolTip(button, Loc.Get("Bridge_WindowsAdapterTip"));
            Grid.SetColumn(button, 1);
            button.Click += async (_, _) => await ChangeDriver(adapter);
            row.Children.Add(button);
            AdapterList.Children.Add(row);
        }
    }

    private async Task ChangeDriver(BridgeAdapter adapter)
    {
        bool assign = adapter.Driver != BridgeDriver.WinUsb;
        // Checked again at the moment of the click: the default adapter can change (one was plugged in).
        _windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync();
        if (assign && WindowsAdapterGuard.IsSameDevice(adapter.InstanceId, _windowsAdapter))
        {
            ShowError(Loc.Get("Bridge_WindowsAdapterTip"));
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format(assign ? "Bridge_AssignTitle" : "Bridge_RestoreTitle", adapter.DisplayName),
            Content = new TextBlock { Text = Loc.Get(assign ? "Bridge_AssignBody" : "Bridge_RestoreBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get(assign ? "Bridge_AssignWinUsb" : "Bridge_RestoreDriver"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Operate(async () =>
        {
            Status.Text = Loc.Get("Bridge_DriverChanging");
            if (assign) await Bridge.AssignWinUsbAsync(adapter.InstanceId);
            else await Bridge.RestoreDriverAsync(adapter.InstanceId);
        });
    }

    private async Task Operate(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        ErrorBar.IsOpen = false;
        Refresh();
        try { await action(); }
        catch (BridgeRequestException ex) { ShowError(ex.BridgeMessage.IsEmpty ? ex.Code : A2dpBridgeText.Format(ex.BridgeMessage)); }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or InvalidDataException
            or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            ShowError(ex is BridgeUnavailableException ? Loc.Get("Bridge_NotConnected") : ex.Message);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void ShowInfo(string message)
    {
        ErrorBar.Severity = InfoBarSeverity.Success;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private async void OnStartCore(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        Status.Text = Loc.Get("Bridge_Starting");
        if (!await Bridge.StartCoreAsync()) ShowError(Loc.Get("Bridge_StartTimeout"));
    });

    private async void OnQuitCore(object sender, RoutedEventArgs e) => await Operate(Bridge.QuitCoreAsync);

    private void OnOpenWindow(object sender, RoutedEventArgs e)
    {
        if (Bridge.Installation is not { } installation) return;
        try
        {
            A2dpBridgeComponent.OpenWindow(installation);
            Bridge.Monitor.Wake();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { ShowError(ex.Message); }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not ProfileChoice profile) return;
        await Operate(() => Bridge.StartStreamAsync(profile.Name));
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await Operate(Bridge.StopStreamAsync);

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) Refresh();
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        // One request when the thumb settles, not one per pixel.
        if (_volumeTimer is null)
        {
            _volumeTimer = DispatcherQueue.CreateTimer();
            _volumeTimer.Interval = TimeSpan.FromMilliseconds(200);
            _volumeTimer.IsRepeating = false;
            _volumeTimer.Tick += async (_, _) =>
            {
                try { await Bridge.SetVolumeAsync((int)Math.Round(Volume.Value)); }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { ShowError(ex.Message); }
            };
        }
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool on = AutoStart.IsOn;
        App.Instance.Host.Settings.Update(s => s with { A2dpBridgeAutoStart = on });
    }

    private async void OnRefreshAdapters(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        _windowsAdapter = await WindowsAdapterGuard.DefaultAdapterInstanceIdAsync();
        await Bridge.RefreshAdaptersAsync();
    });

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        string? folder = Bridge.Status.Snapshot?.ConfigDirectory;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try { using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { ShowError(ex.Message); }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _download?.Cancel();

    private async void OnDownload(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        Progress.Visibility = Cancel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Status.Text = Loc.Get("Bridge_Downloading");
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (_download != cancellation) return;
                Progress.IsIndeterminate = false;
                Progress.Value = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
                Status.Text = Loc.Format("Bridge_DownloadProgress", p.Received / 1048576.0, p.Total / 1048576.0);
            });
            await Bridge.InstallAsync(progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { ShowError(Loc.Get("Bridge_Cancelled")); }
        finally
        {
            _download = null;
            Progress.Visibility = Cancel.Visibility = Visibility.Collapsed;
        }
    });

    private static string Origin(BridgeOrigin? origin) => Loc.Get(origin switch
    {
        BridgeOrigin.Managed => "Bridge_OriginManaged",
        BridgeOrigin.Installed => "Bridge_OriginInstalled",
        _ => "Bridge_OriginRunning",
    });

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
