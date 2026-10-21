// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EncoDeck.App.Controls;

/// <summary>
/// One battery level of the connection pop-up, drawn like HeyMelody's discovery dialog
/// (DiscoveryBatteryViewWrapper): the level fills the battery in the fill colour, red at 20 % or
/// less; the flash shows while charging. Horizontal: "80%" under the battery. Vertical (the column
/// style): device icon, battery, "80".
/// </summary>
public sealed partial class PopupBatteryLevel : UserControl
{
    /// <summary>HeyMelody's level colours (<c>melody_ui_iot_link_progress</c> / <c>…_red</c>).</summary>
    public static readonly Color Green = Color.FromArgb(0xFF, 0x2D, 0xC8, 0x4E);
    public static readonly Color Red = Color.FromArgb(0xFF, 0xEC, 0x3E, 0x50);

    public PopupBatteryLevel()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="level"/> (≤ 0: unknown, "--").</summary>
    public void Show(BatteryComponent component, int level, bool charging, bool vertical, Brush text, Brush outline, Color fill)
    {
        Horizontal.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        Vertical.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        bool known = level is > 0 and <= 100;
        var fillBrush = new SolidColorBrush(known && level <= 20 ? Red : fill);
        double amount = known ? level / 100.0 : 0;
        string number = level.ToString(CultureInfo.CurrentCulture);
        if (vertical)
        {
            VIcon.Component = component;
            VIcon.Foreground = outline;
            VOutline.Stroke = outline;
            VNub.Fill = outline;
            VFill.Fill = fillBrush;
            VFill.Width = 17.33 * amount;
            VFlash.Fill = outline;
            VFlash.Visibility = known && charging ? Visibility.Visible : Visibility.Collapsed;
            VText.Text = known ? number : "--";
            VText.Foreground = text;
        }
        else
        {
            HOutline.Stroke = outline;
            HFill.Fill = fillBrush;
            HFill.Width = 17.66 * amount;
            // The flash takes the level colour (it is tinted like the fill in HeyMelody).
            HFlash.Fill = new SolidColorBrush(fill);
            HFlash.Visibility = known && charging ? Visibility.Visible : Visibility.Collapsed;
            HText.Text = known ? number + "%" : "--";
            HText.Foreground = text;
        }
        AutomationProperties.SetName(this, known ? $"{component} {number}%" : $"{component}");
    }
}
