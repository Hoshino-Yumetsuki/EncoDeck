// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>Battery glyph, level, label and "last read" freshness for one component.</summary>
public sealed partial class BatteryIndicator : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(BatteryItemViewModel), typeof(BatteryIndicator), new PropertyMetadata(null));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(BatteryIndicator), new PropertyMetadata(28.0));

    public static readonly DependencyProperty LevelFontSizeProperty = DependencyProperty.Register(
        nameof(LevelFontSize), typeof(double), typeof(BatteryIndicator), new PropertyMetadata(20.0));

    public BatteryIndicator()
    {
        InitializeComponent();
    }

    public BatteryItemViewModel? Item
    {
        get => (BatteryItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public double LevelFontSize
    {
        get => (double)GetValue(LevelFontSizeProperty);
        set => SetValue(LevelFontSizeProperty, value);
    }

    public double StaleOpacity(bool isStale) => isStale ? 0.55 : 1.0;
}
