// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>Small line icon for a battery: L or R in a ring for the earbuds, the case outline for the case.</summary>
public sealed partial class ComponentIcon : UserControl
{
    public static readonly DependencyProperty ComponentProperty = DependencyProperty.Register(
        nameof(Component), typeof(BatteryComponent), typeof(ComponentIcon),
        new PropertyMetadata(BatteryComponent.Left, (d, _) => ((ComponentIcon)d).Update()));

    public ComponentIcon()
    {
        InitializeComponent();
        Update();
    }

    public BatteryComponent Component
    {
        get => (BatteryComponent)GetValue(ComponentProperty);
        set => SetValue(ComponentProperty, value);
    }

    private void Update()
    {
        bool isCase = Component == BatteryComponent.Case;
        Bud.Visibility = isCase ? Visibility.Collapsed : Visibility.Visible;
        Case.Visibility = isCase ? Visibility.Visible : Visibility.Collapsed;
        Letter.Text = Component == BatteryComponent.Right ? "R" : "L";
    }
}
