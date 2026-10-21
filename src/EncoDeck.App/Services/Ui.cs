// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;

namespace EncoDeck.App.Services;

/// <summary>x:Bind helper functions.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(bool a, bool b) => a || b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool And(bool a, bool b) => a && b;

    /// <summary>Percent readout: "100%".</summary>
    public static string Percent(double value) => value.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%";

    /// <summary>Slider value readout: "4", "4.5".</summary>
    public static string Number(double value) => value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
}
