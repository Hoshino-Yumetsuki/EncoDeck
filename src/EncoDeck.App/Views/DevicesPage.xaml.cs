// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Instance.Host.Devices.KnownDevicesChanged += OnDevicesChanged;
            Build();
        };
        Unloaded += (_, _) => App.Instance.Host.Devices.KnownDevicesChanged -= OnDevicesChanged;
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    private void OnDevicesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Build);

    private void Build()
    {
        DeviceHost.Children.Clear();
        var devices = App.Instance.Host.Devices.KnownDevices;
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var device in devices)
        {
            DeviceHost.Children.Add(CreateRow(device));
        }
    }

    private UIElement CreateRow(KnownDevice device)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = device.Identity.Name });
        string status = device.IsConnected ? Loc.Get("Devices_StatusConnected") : Loc.Get("Devices_StatusNotConnected");
        string model = device.Profile.DisplayName;
        text.Children.Add(new TextBlock
        {
            Text = $"{model} · {status} · {device.Identity.Address.ToRedactedString()}",
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        });
        Grid.SetColumn(text, 1);

        UIElement action;
        if (device.IsActive)
        {
            action = new TextBlock { Text = Loc.Get("Devices_Active"), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
        }
        else
        {
            var button = new Button { Content = Loc.Get("Devices_UseThis"), VerticalAlignment = VerticalAlignment.Center };
            button.Click += async (_, _) =>
            {
                App.Instance.Host.Settings.Update(s => s with { PreferredDeviceId = device.Identity.DeviceId });
                await App.Instance.Host.Devices.SelectDeviceAsync(device.Identity.DeviceId);
            };
            action = button;
        }
        Grid.SetColumn((FrameworkElement)action, 2);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(action);
        return grid;
    }

    private async void OnOpenBluetoothSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth"));

    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = await DiagnosticsExporter.ExportAsync(App.Instance.Host);
            ExportResult.Text = Loc.Format("Devices_ExportDone", path);
            DiagnosticsExporter.RevealInExplorer(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportResult.Text = Loc.Format("Devices_ExportFailed", ex.Message);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.Instance.Host.Paths.LogDirectory) { UseShellExecute = true });
}
