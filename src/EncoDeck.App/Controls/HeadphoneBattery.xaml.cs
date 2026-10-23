// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace EncoDeck.App.Controls;

public sealed partial class HeadphoneBattery : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(BatteryItemViewModel), typeof(HeadphoneBattery), new PropertyMetadata(null));
    public BatteryItemViewModel? Item
    {
        get => (BatteryItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
    public HeadphoneBattery() => InitializeComponent();
}
