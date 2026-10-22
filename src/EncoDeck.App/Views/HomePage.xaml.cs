// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EncoDeck.App.Views;

/// <summary>
/// HeyMelody's device page laid out for a landscape window: the device stage (name, levels,
/// rotatable 3D model) on the left; noise control, device management and sound on the right.
/// </summary>
public sealed partial class HomePage : Page
{
    private string _devicesSignature = string.Empty;
    private bool _syncing;

    public HomePage()
    {
        InitializeComponent();
        AutomationProperties.SetName(Model, Loc.Get("Home_Model_Name"));
        Loaded += (_, _) =>
        {
            Features.Changed += OnFeaturesChanged;
            Sync();
        };
        Unloaded += (_, _) => Features.Changed -= OnFeaturesChanged;
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private FeaturesViewModel Features => App.Instance.Features;

    private void OnFeaturesChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        var f = Features;
        bool ready = f.IsReady;
        _syncing = true;
        try
        {
            SyncDevices(f, ready);

            // Rows follow the (cached) model capabilities only, not whether the live value has been
            // read yet, so the layout is the same right after a restart as once connected.
            bool spatial = f.Has(DeviceCapability.SpatialAudio);
            SpatialRow.Visibility = spatial ? Visibility.Visible : Visibility.Collapsed;
            SpatialValue.Text = f.State.Spatial is null ? string.Empty : f.SpatialMode switch
            {
                SpatialAudioMode.Fixed => Loc.Get("Spatial_Fixed"),
                SpatialAudioMode.HeadTracking => Loc.Get("Spatial_HeadTracking"),
                _ => Loc.Get("Spatial_Off"),
            };

            bool game = f.Has(DeviceCapability.GamingMode) && f.HasSwitch(DeviceToggle.GamingMode);
            GameRow.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
            bool? on = f.Toggle(DeviceToggle.GamingMode);
            GameSwitch.IsOn = on == true;
            GameSwitch.IsEnabled = ready && on is not null && !f.IsPending("toggle:");
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---- device management (HeyMelody's 设备管理 card) --------------------------------------------

    private void SyncDevices(FeaturesViewModel f, bool ready)
    {
        // The connected-device list is read with OPPO's multi-connect commands.
        bool available = f.Has(DeviceCapability.DualDeviceConnection) && f.Model is not null;
        DevicesCard.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        // Keep the card (and its height) while the list has not been read yet.
        if (!available || f.State.SourceDevices is null) return;

        var devices = f.SourceDevices;
        string signature = string.Join('|', devices.Select(d => $"{d.AddressText}/{d.State}/{d.IsAudioActive}/{d.Name}")) + $"#{ready}";
        if (signature == _devicesSignature) return;
        _devicesSignature = signature;

        DeviceHost.Children.Clear();
        DeviceHost.ColumnDefinitions.Clear();
        int column = 0;
        foreach (var device in devices)
        {
            DeviceHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = DeviceCell(device, ready);
            Grid.SetColumn(cell, column++);
            DeviceHost.Children.Add(cell);
        }
    }

    /// <summary>
    /// A round device icon (accent when connected) with its name and state. Tapping a disconnected
    /// device connects it, tapping a connected one disconnects it (after a confirmation); this PC is
    /// disconnected from Windows Bluetooth settings, as the official client does for the phone.
    /// </summary>
    private Button DeviceCell(SourceDevice device, bool ready)
    {
        // Device type icons: 1 phone, 2 computer, 3 tablet, 4 watch.
        string glyph = device.DeviceType switch { 1 => "", 2 => "", 3 => "", 4 => "", _ => "" };
        bool connected = device.IsConnected;
        var circle = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.Resources[connected ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush"],
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 22,
                Foreground = (Brush)Application.Current.Resources[connected ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            },
        };
        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.Name) ? device.AddressText : device.Name,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[connected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
        };
        bool busy = device.State is 1 or 4;
        var status = new TextBlock
        {
            Text = Loc.Get(device.IsThisDevice ? "Home_DeviceThisPc" : busy ? "Home_DeviceBusy" : connected ? "Home_DeviceConnected" : "Home_DeviceTapToConnect"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        };
        var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(circle);
        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(name);
        label.Children.Add(status);
        content.Children.Add(label);
        var cell = new Button
        {
            Content = content,
            Tag = device,
            // Subtle: transparent at rest and when disabled (connected devices are not tappable).
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Every cell is as tall as the row and starts at the top, so the circles line up even
            // when one name wraps to two lines.
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(4, 8, 4, 8),
            IsEnabled = ready && !busy,
        };
        AutomationProperties.SetName(cell, $"{name.Text}, {status.Text}");
        ToolTipService.SetToolTip(cell, Loc.Get(device.IsThisDevice ? "Home_DeviceThisPc_Hint" : connected ? "Dual_Disconnect" : "Dual_Connect"));
        cell.Click += OnDeviceClick;
        return cell;
    }

    private async void OnDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceDevice device }) return;
        string name = string.IsNullOrWhiteSpace(device.Name) ? device.AddressText : device.Name;
        if (device.IsThisDevice)
        {
            var hint = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Home_DeviceThisPc_Title"),
                Content = new TextBlock { Text = Loc.Get("Home_DeviceThisPc_Message"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Home_OpenBluetoothSettings"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await hint.ShowAsync() == ContentDialogResult.Primary) await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));
            return;
        }
        if (device.IsConnected)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("Home_DeviceDisconnect_Title", name),
                Content = new TextBlock { Text = Loc.Get("Home_DeviceDisconnect_Message"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Dual_Disconnect"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            _devicesSignature = string.Empty;
            await Features.MultiConnectAsync(MultiConnectOperation.Disconnect, device);
            Sync();
            return;
        }
        // Official: with two devices already connected, connecting a third drops one of them.
        if (Features.SourceDevices.Count(d => d.IsConnected) >= 2)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("Dual_ConnectNew_Title"),
                Content = new TextBlock { Text = Loc.Format("Dual_ConnectNew_Message", name), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Dual_Connect"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _devicesSignature = string.Empty;
        await Features.MultiConnectAsync(MultiConnectOperation.Connect, device);
        Sync();
    }

    // ---- sound -----------------------------------------------------------------------------------

    private async void OnGameToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool requested = GameSwitch.IsOn;
        Sync(); // the device decides; snap back until it confirms
        await Features.SetGameModeAsync(requested);
        Sync();
    }

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>Landscape: the device stage on the left and its controls on the right; narrow: stacked.</summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 820;
        Grid.SetColumnSpan(Stage, wide ? 1 : 2);
        Grid.SetRow(SidePanel, wide ? 1 : 2);
        Grid.SetColumn(SidePanel, wide ? 1 : 0);
        Grid.SetColumnSpan(SidePanel, wide ? 1 : 2);
        Model.Height = wide ? 440 : 320;
    }

    // ---- navigation ------------------------------------------------------------------------------

    private async void OnOpenBluetoothSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));

    private void OnOpenDiagnostics(object sender, RoutedEventArgs e) => App.Instance.Windows.ShowMain("Devices");

    private void OnOpenSound(object sender, RoutedEventArgs e) => App.Instance.Windows.ShowMain("Sound");

    private void OnOpenControls(object sender, RoutedEventArgs e) => App.Instance.Windows.ShowMain("Controls");
}
