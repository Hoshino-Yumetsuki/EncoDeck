// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Devices;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Experimental: switches OPPO earbuds whose firmware answers the LE/BR switch query (or that are
/// already bonded over LE) between Windows' Classic audio and LE Audio (LC3). See docs/LE_AUDIO.md.
/// </summary>
public sealed partial class LeAudioSettings : UserControl
{
    private CancellationTokenSource? _operation;
    private LeAudioBondState? _bond;
    private string? _deviceId;
    private ulong _address;
    private bool _busy;
    private bool _dialogOpen;

    public LeAudioSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Switch.Content = Loc.Get("LeAudio_Switch");
            Restore.Content = Loc.Get("LeAudio_Restore");
            Cancel.Content = Loc.Get("Common_Cancel");
            Features.Changed += OnChanged;
            _deviceId = null;
            Refresh();
        };
        Unloaded += (_, _) => Features.Changed -= OnChanged;
    }

    private static FeaturesViewModel Features => App.Instance.Features;

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        // Unpairing removes the device from the app mid-operation; keep the card until it finishes.
        if (_busy) return;
        var state = Features.State;
        if (state.Profile is not OppoDeviceProfile || state.Device is not { Transport: DeviceTransport.Bluetooth } device)
        {
            // After an operation the earbuds may have left the app (pairing removed): keep the card,
            // its result and its buttons, which still work on the remembered address.
            Visibility = _address != 0 && Message.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            Sync();
            return;
        }
        if (device.DeviceId != _deviceId)
        {
            _deviceId = device.DeviceId;
            _address = device.Address.Value;
            _bond = null;
            _ = LoadBondAsync();
        }
        Visibility = state.LeAudioSwitch is not null || _bond?.PrimaryBonded == true ? Visibility.Visible : Visibility.Collapsed;
        Sync();
    }

    private async Task LoadBondAsync()
    {
        ulong address = _address;
        try
        {
            var bond = await LeAudioPairing.GetStateAsync(address);
            if (address != _address) return;
            _bond = bond;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _bond = null;
        }
        if (!_busy) Refresh();
    }

    private void Sync()
    {
        bool le = _bond?.PrimaryBonded == true;
        bool both = le && _bond!.SecondaryBonded;
        Status.Text = _bond is null ? "" : Loc.Get(both ? "LeAudio_StatusLe" : le ? "LeAudio_StatusLeSingle" : "LeAudio_StatusClassic");
        // Nothing bonded (e.g. an earlier attempt failed): offer both ways back.
        bool unpaired = _bond is { PrimaryBonded: false, ClassicBonded: false };
        Switch.Visibility = both ? Visibility.Collapsed : Visibility.Visible;
        Restore.Visibility = le || unpaired ? Visibility.Visible : Visibility.Collapsed;
        Switch.IsEnabled = Restore.IsEnabled = !_busy && !_dialogOpen && _bond is not null;
        Progress.Visibility = Cancel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSwitch(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("LeAudio_ConfirmSwitch", "LeAudio_Switch"))
            await RunAsync(LeAudioPairing.SwitchToLeAudioAsync);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("LeAudio_ConfirmRestore", "LeAudio_Restore"))
            await RunAsync(LeAudioPairing.RestoreClassicAsync);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async Task RunAsync(Func<ulong, IProgress<LeAudioPairingStage>, CancellationToken, Task<LeAudioPairingResult>> operation)
    {
        if (_busy) return;
        _busy = true;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        Message.Visibility = Visibility.Visible;
        Sync();
        try
        {
            var progress = new Progress<LeAudioPairingStage>(stage => Message.Text = Loc.Get($"LeAudio_Stage_{stage}"));
            var result = await operation(_address, progress, cancellation.Token);
            Message.Text = Loc.Get($"LeAudio_Result_{result.Outcome}") + (result.Detail is { } detail ? $" ({detail})" : "");
        }
        catch (OperationCanceledException)
        {
            Message.Text = Loc.Get("LeAudio_Cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message.Text = Loc.Format("LeAudio_Error", ex.Message);
        }
        finally
        {
            _operation = null;
            _busy = false;
            _bond = null;
            Sync();
            _ = LoadBondAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string messageKey, string primaryKey)
    {
        // A second ContentDialog while one is open throws; a double click must not open two.
        if (_dialogOpen || _busy) return false;
        _dialogOpen = true;
        Sync();
        try
        {
            return await ShowConfirmAsync(messageKey, primaryKey);
        }
        finally
        {
            _dialogOpen = false;
            Sync();
        }
    }

    private async Task<bool> ShowConfirmAsync(string messageKey, string primaryKey)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("LeAudio_ConfirmTitle"),
            Content = new TextBlock { Text = Loc.Get(messageKey), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get(primaryKey),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
