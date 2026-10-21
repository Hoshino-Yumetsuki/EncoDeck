// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EncoDeck.App.Controls;

/// <summary>Shows the model's product image when one is installed, otherwise the generic illustration.</summary>
public sealed partial class DeviceImage : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(DeviceImage), new PropertyMetadata(null, (d, _) => ((DeviceImage)d).Update()));

    public DeviceImage()
    {
        InitializeComponent();
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    private void Update()
    {
        if (SourcePath is { Length: > 0 } path)
        {
            Picture.Source = new BitmapImage(new Uri(path));
            Picture.Visibility = Visibility.Visible;
            Illustration.Visibility = Visibility.Collapsed;
        }
        else
        {
            Picture.Source = null;
            Picture.Visibility = Visibility.Collapsed;
            Illustration.Visibility = Visibility.Visible;
        }
    }
}
