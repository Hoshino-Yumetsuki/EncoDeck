// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Media;
using EncoDeck.Platform.Shell;
using CommunityToolkit.WinUI.Lottie;
using Windows.Storage;
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
    private string? _animationPath;
    private int _loadVersion;
    public bool LoopVideo { get; set; } = true;
    internal string DiagnosticState => $"lottie={Animation.IsAnimatedVisualLoaded} playing={Animation.IsPlaying} videoFrames={_video?.Player?.FramesPublished} alphaRight={_playing?.EndsWith(".alpha-right.mp4", StringComparison.OrdinalIgnoreCase)} still={Still.Visibility} webp=[{WebpAnimation.DiagnosticState}]";
    public void Stop() { ReleaseVideo(); ReleaseAnimation(); ReleaseWebp(); }

    public static readonly DependencyProperty FallbackPathProperty = DependencyProperty.Register(
        nameof(FallbackPath), typeof(string), typeof(DeviceMedia), new PropertyMetadata(null, (d, _) => ((DeviceMedia)d).Update()));
    public string? FallbackPath
    {
        get => (string?)GetValue(FallbackPathProperty);
        set => SetValue(FallbackPathProperty, value);
    }

    public DeviceMedia()
    {
        InitializeComponent();
        WebpAnimation.Loop = false;
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) Resume();
            else Pause();
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
        if (SourcePath is { Length: > 0 } webpPath && Path.GetExtension(webpPath).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            ReleaseVideo(); ReleaseAnimation();
            WebpAnimation.ImagePath = FallbackPath ?? webpPath;
            WebpAnimation.ModelPath = webpPath;
            WebpAnimation.Visibility = Visibility.Visible;
            Still.Visibility = Visibility.Collapsed;
            return;
        }
        ReleaseWebp();
        if (SourcePath is { Length: > 0 } animationPath && Path.GetExtension(animationPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            ReleaseVideo();
            Still.SourcePath = FallbackPath;
            Still.Visibility = Visibility.Visible;
            if (IsLoaded && _animationPath != animationPath) _ = LoadAnimationAsync(animationPath);
            return;
        }
        ReleaseAnimation();
        if (IsVideo && SourcePath is { Length: > 0 } path && File.Exists(path))
        {
            Video.Visibility = Visibility.Visible;
            Still.Visibility = Visibility.Collapsed;
            if (!IsLoaded || _playing == path) return;
            _video ??= new SilentVideoPresenter(Video);
            _video.Play(path, LoopVideo ? VideoSegment.WholeLooping : VideoSegment.Whole, maxHeight: 480,
                alphaRight: path.EndsWith(".alpha-right.mp4", StringComparison.OrdinalIgnoreCase));
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

    private async Task LoadAnimationAsync(string path)
    {
        ReleaseAnimation();
        int version = _loadVersion;
        _animationPath = path;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var source = new LottieVisualSource();
            await source.SetSourceAsync(file);
            if (version != _loadVersion || !IsLoaded) return;
            Animation.Source = source;
            if (!Animation.IsAnimatedVisualLoaded) { Animation.Source = null; return; }
            Animation.Visibility = Visibility.Visible;
            Still.Visibility = Visibility.Collapsed;
            if (!ShellEnvironment.AnimationsEnabled()) Animation.SetProgress(1);
            else if (Visibility == Visibility.Visible) _ = Animation.PlayAsync(0, 1, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (version == _loadVersion) { Animation.Source = null; Animation.Visibility = Visibility.Collapsed; Still.Visibility = Visibility.Visible; }
        }
    }

    public void Pause() { _video?.Pause(); Animation.Pause(); WebpAnimation.PauseAnimation(); }
    public void Resume()
    {
        _video?.Resume();
        WebpAnimation.ResumeAnimation();
        if (ShellEnvironment.AnimationsEnabled()) Animation.Resume();
    }

    private void ReleaseAnimation()
    {
        ++_loadVersion;
        _animationPath = null;
        Animation.Stop();
        Animation.Source = null;
        Animation.Visibility = Visibility.Collapsed;
    }

    private void ReleaseWebp()
    {
        WebpAnimation.PauseAnimation();
        WebpAnimation.ModelPath = null;
        WebpAnimation.Visibility = Visibility.Collapsed;
    }

    private void ReleaseVideo()
    {
        _video?.Dispose();
        _video = null;
        _playing = null;
        Video.Visibility = Visibility.Collapsed;
    }
}
