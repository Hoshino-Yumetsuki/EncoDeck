// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Media;
using EncoDeck.Platform.Shell;
using CommunityToolkit.WinUI.Lottie;
using Windows.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using System.Diagnostics;

namespace EncoDeck.App.Controls;

/// <summary>
/// Shows a device animation: a video (silent, looping — decoded without audio, see
/// <see cref="SilentVideoPresenter"/>) or an image (GIF animates automatically), falling back to the
/// generic illustration. Decoding runs only while the control is loaded and visible.
/// </summary>
public sealed partial class DeviceMedia : UserControl
{
    public static readonly DependencyProperty IsHeadphonesProperty = DependencyProperty.Register(
        nameof(IsHeadphones), typeof(bool), typeof(DeviceMedia), new PropertyMetadata(false, (d, e) =>
        {
            var media = (DeviceMedia)d;
            media.Still.IsHeadphones = (bool)e.NewValue;
            media.StopFallback();
            media.StartFallback();
        }));

    /// <summary>Selects the headphones illustration when no product image is available.</summary>
    public bool IsHeadphones
    {
        get => (bool)GetValue(IsHeadphonesProperty);
        set => SetValue(IsHeadphonesProperty, value);
    }

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(DeviceMedia), new PropertyMetadata(null, (d, _) => ((DeviceMedia)d).Update()));
    public static readonly DependencyProperty DarkSourcePathProperty = DependencyProperty.Register(
        nameof(DarkSourcePath), typeof(string), typeof(DeviceMedia), new PropertyMetadata(null, (d, _) => ((DeviceMedia)d).Update()));
    public string? DarkSourcePath { get => (string?)GetValue(DarkSourcePathProperty); set => SetValue(DarkSourcePathProperty, value); }

    public static readonly DependencyProperty IsVideoProperty = DependencyProperty.Register(
        nameof(IsVideo), typeof(bool), typeof(DeviceMedia), new PropertyMetadata(false, (d, _) => ((DeviceMedia)d).Update()));

    private SilentVideoPresenter? _video;
    private string? _playing;
    private string? _animationPath;
    private int _loadVersion;
    private bool _fallback;
    private bool _fallbackRunning;
    private string? _webpPath;
    private bool? _webpAvailable;
    private readonly Stopwatch _fallbackClock = new();
    public bool LoopVideo { get; set; } = true;
    internal bool IsVideoHolding => _video?.Player?.IsHolding == true;
    internal string DiagnosticState => $"generic={_fallback} genericRunning={_fallbackRunning} genericMs={_fallbackClock.ElapsedMilliseconds} lottie={Animation.IsAnimatedVisualLoaded} playing={Animation.IsPlaying} videoFile={Path.GetFileName(_playing)} videoFrames={_video?.Player?.FramesPublished} alphaRight={_playing?.EndsWith(".alpha-right.mp4", StringComparison.OrdinalIgnoreCase)} still={Still.Visibility} webp=[{WebpAnimation.DiagnosticState}]";
    public void Stop() { ReleaseVideo(); ReleaseAnimation(); ReleaseWebp(); StopFallback(); }

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
        WebpAnimation.ModelAvailabilityChanged += (_, available) =>
        {
            _webpAvailable = available;
            if (available) return;
            if (Path.GetExtension(SourcePath ?? "").Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                WebpAnimation.Visibility = Visibility.Collapsed;
                Still.SourcePath = FallbackPath ?? SourcePath;
                Still.Visibility = Visibility.Visible;
                _fallback = true;
                StartFallback();
            }
        };
        Loaded += (_, _) => Update();
        ActualThemeChanged += (_, _) => Update();
        Still.SizeChanged += (_, _) => ElementCompositionPreview.GetElementVisual(Still).CenterPoint = new Vector3((float)Still.ActualWidth / 2, (float)Still.ActualHeight / 2, 0);
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
        _fallback = false;
        StopFallback();
        if (SourcePath is { Length: > 0 } webpPath && Path.GetExtension(webpPath).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            ReleaseVideo(); ReleaseAnimation();
            if (_webpPath != webpPath) { _webpPath = webpPath; _webpAvailable = null; }
            WebpAnimation.ImagePath = FallbackPath ?? webpPath;
            WebpAnimation.ModelPath = webpPath;
            WebpAnimation.Visibility = _webpAvailable == false ? Visibility.Collapsed : Visibility.Visible;
            Still.Visibility = _webpAvailable == false ? Visibility.Visible : Visibility.Collapsed;
            if (_webpAvailable == false) { Still.SourcePath = FallbackPath ?? webpPath; _fallback = true; StartFallback(); }
            return;
        }
        ReleaseWebp();
        if (SourcePath is { Length: > 0 } animationPath && Path.GetExtension(animationPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            ReleaseVideo();
            Still.SourcePath = FallbackPath;
            Still.Visibility = Visibility.Visible;
            if (_animationPath == animationPath && Animation.IsAnimatedVisualLoaded) { Still.Visibility = Visibility.Collapsed; return; }
            if (IsLoaded && _animationPath != animationPath) _ = LoadAnimationAsync(animationPath);
            return;
        }
        ReleaseAnimation();
        string? videoPath = ActualTheme == ElementTheme.Dark && !string.IsNullOrEmpty(DarkSourcePath) && File.Exists(DarkSourcePath) ? DarkSourcePath : SourcePath;
        if (ActualTheme == ElementTheme.Dark && SourcePath?.EndsWith(".device-once.mp4", StringComparison.OrdinalIgnoreCase) == true
            && (string.IsNullOrEmpty(DarkSourcePath) || !File.Exists(DarkSourcePath)))
        {
            ReleaseVideo(); Still.SourcePath = FallbackPath; Still.Visibility = Visibility.Visible;
            _fallback = true; StartFallback(); return;
        }
        if (IsVideo && videoPath is { Length: > 0 } path && File.Exists(path))
        {
            Video.Visibility = Visibility.Visible;
            Still.Visibility = Visibility.Collapsed;
            if (!IsLoaded || _playing == path) return;
            _video ??= new SilentVideoPresenter(Video);
            _video.Play(path, LoopVideo && !path.EndsWith(".device-once.mp4", StringComparison.OrdinalIgnoreCase) ? VideoSegment.WholeLooping : VideoSegment.Whole, maxHeight: 480,
                alphaRight: path.EndsWith(".alpha-right.mp4", StringComparison.OrdinalIgnoreCase));
            _playing = path;
            if (Visibility != Visibility.Visible) _video.Pause();
        }
        else
        {
            ReleaseVideo();
            // Compiled XAML bindings normalize a null string to "".
            Still.SourcePath = string.IsNullOrEmpty(SourcePath) ? FallbackPath : SourcePath;
            Still.Visibility = Visibility.Visible;
            _fallback = true;
            StartFallback();
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
            if (!Animation.IsAnimatedVisualLoaded) { Animation.Source = null; _fallback = true; StartFallback(); return; }
            Animation.Visibility = Visibility.Visible;
            Still.Visibility = Visibility.Collapsed;
            if (!ShellEnvironment.AnimationsEnabled()) Animation.SetProgress(1);
            else if (Visibility == Visibility.Visible) _ = Animation.PlayAsync(0, 1, LoopVideo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (version == _loadVersion) { Animation.Source = null; Animation.Visibility = Visibility.Collapsed; Still.Visibility = Visibility.Visible; _fallback = true; StartFallback(); }
        }
    }

    public void Pause() { _video?.Pause(); Animation.Pause(); WebpAnimation.PauseAnimation(); StopFallback(); }
    public void Resume()
    {
        _video?.Resume();
        WebpAnimation.ResumeAnimation();
        if (ShellEnvironment.AnimationsEnabled()) Animation.Resume();
        StartFallback();
    }

    // Native product motion also works offline and for models without an official animation.
    // It moves the existing shot/illustration; it does not invent a case-opening sequence.
    private void StartFallback()
    {
        if (IsHeadphones || !_fallback || _fallbackRunning || !IsLoaded || Visibility != Visibility.Visible || !ShellEnvironment.AnimationsEnabled()) return;
        var visual = ElementCompositionPreview.GetElementVisual(Still);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(Still, true);
        var move = c.CreateVector3KeyFrameAnimation();
        var scale = c.CreateVector3KeyFrameAnimation();
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(.2f, 1));
        if (LoopVideo)
        {
            move.InsertKeyFrame(0, Vector3.Zero); move.InsertKeyFrame(.5f, new Vector3(0, -5, 0), ease); move.InsertKeyFrame(1, Vector3.Zero, ease);
            scale.InsertKeyFrame(0, Vector3.One); scale.InsertKeyFrame(.5f, new Vector3(1.012f, 1.012f, 1), ease); scale.InsertKeyFrame(1, Vector3.One, ease);
            move.Duration = scale.Duration = TimeSpan.FromSeconds(4);
            move.IterationBehavior = scale.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        }
        else
        {
            move.InsertKeyFrame(0, new Vector3(0, 16, 0)); move.InsertKeyFrame(1, Vector3.Zero, ease);
            scale.InsertKeyFrame(0, new Vector3(.88f, .88f, 1)); scale.InsertKeyFrame(1, Vector3.One, ease);
            move.Duration = scale.Duration = TimeSpan.FromMilliseconds(850);
        }
        visual.StartAnimation("Translation", move); visual.StartAnimation("Scale", scale);
        _fallbackRunning = true;
        _fallbackClock.Restart();
    }

    private void StopFallback()
    {
        if (!_fallbackRunning) return;
        var visual = ElementCompositionPreview.GetElementVisual(Still);
        visual.StopAnimation("Translation"); visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _fallbackRunning = false;
        _fallbackClock.Stop();
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
        _webpPath = null;
        _webpAvailable = null;
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
