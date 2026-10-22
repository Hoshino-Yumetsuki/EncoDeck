// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>Generic product illustration: earbuds with their case, or headphones.</summary>
public sealed partial class EarbudsIllustration : UserControl
{
    public static readonly DependencyProperty IsHeadphonesProperty = DependencyProperty.Register(
        nameof(IsHeadphones), typeof(bool), typeof(EarbudsIllustration), new PropertyMetadata(false, (d, e) => ((EarbudsIllustration)d).Update((bool)e.NewValue)));

    public EarbudsIllustration()
    {
        InitializeComponent();
    }

    public bool IsHeadphones
    {
        get => (bool)GetValue(IsHeadphonesProperty);
        set => SetValue(IsHeadphonesProperty, value);
    }

    private void Update(bool headphones)
    {
        Earbuds.Visibility = headphones ? Visibility.Collapsed : Visibility.Visible;
        Headphones.Visibility = headphones ? Visibility.Visible : Visibility.Collapsed;
    }
}
