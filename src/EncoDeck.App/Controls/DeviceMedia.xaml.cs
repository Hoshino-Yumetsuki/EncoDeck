// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Shows a device animation: a video (silent, looping — decoded without audio, see
/// <see cref="SilentVideoPresenter"/>) or an image (GIF animates automatically), falling back to the
/// generic illustration. Decoding runs only while the control is loaded and visible.
/// </summary>
public sealed partial class DeviceMedia : UserControl
{
    public static readonly DependencyProperty IsHeadphonesProperty = DependencyProperty.Register(
        nameof(IsHeadphones), typeof(bool), typeof(DeviceMedia), new PropertyMetadata(false, (d, e) => ((DeviceMedia)d).Still.IsHeadphones = (bool)e.NewValue));

    /// <summary>Selects the headphones illustration when no product image is available.</summary>
    public bool IsHeadphones
    {
        get => (bool)GetValue(IsHeadphonesProperty);
        set => SetValue(IsHeadphonesProperty, value);
    }

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(DeviceMedia), new PropertyMetadata(null, (d, _) => ((DeviceMedia)d).Update()));

    public static readonly DependencyProperty IsVideoProperty = DependencyProperty.Register(
        nameof(IsVideo), typeof(bool), typeof(DeviceMedia), new PropertyMetadata(false, (d, _) => ((DeviceMedia)d).Update()));

    private SilentVideoPresenter? _video;
    private string? _playing;

    public DeviceMedia()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => ReleaseVideo();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) _video?.Resume();
            else _video?.Pause();
        });
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public bool IsVideo
    {
        get => (bool)GetValue(IsVideoProperty);
        set => SetValue(IsVideoProperty, value);
    }

    private void Update()
    {
        if (IsVideo && SourcePath is { Length: > 0 } path && File.Exists(path))
        {
            Video.Visibility = Visibility.Visible;
            Still.Visibility = Visibility.Collapsed;
            if (!IsLoaded || _playing == path) return;
            _video ??= new SilentVideoPresenter(Video);
            _video.Play(path, VideoSegment.WholeLooping);
            _playing = path;
            if (Visibility != Visibility.Visible) _video.Pause();
        }
        else
        {
            ReleaseVideo();
            Still.SourcePath = SourcePath;
            Still.Visibility = Visibility.Visible;
        }
    }

    private void ReleaseVideo()
    {
        _video?.Dispose();
        _video = null;
        _playing = null;
        Video.Visibility = Visibility.Collapsed;
    }
}
