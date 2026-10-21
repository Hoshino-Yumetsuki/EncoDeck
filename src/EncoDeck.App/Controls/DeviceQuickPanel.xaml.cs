// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Device name, batteries and noise control: the content of the tray panel and of the panel on
/// top of Windows 11 Quick Settings.
/// </summary>
public sealed partial class DeviceQuickPanel : UserControl
{
    public DeviceQuickPanel()
    {
        InitializeComponent();
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    /// <summary>The "open EncoDeck" button was clicked.</summary>
    public event EventHandler? OpenAppRequested;

    private void OnOpenApp(object sender, RoutedEventArgs e) => OpenAppRequested?.Invoke(this, EventArgs.Empty);
}
