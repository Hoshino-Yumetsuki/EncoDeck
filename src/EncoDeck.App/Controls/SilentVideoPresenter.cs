// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Platform.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EncoDeck.App.Controls;

/// <summary>
/// Shows a <see cref="SilentVideoPlayer"/> in an <see cref="Image"/>: frames are copied into one
/// <see cref="WriteableBitmap"/> on the UI thread, at most one pending copy at a time. This replaces
/// <c>MediaPlayerElement</c> for device animations: no audio, no media session, nothing in the system
/// media controls, nothing media keys or earbud taps can pause.
/// </summary>
internal sealed class SilentVideoPresenter : IDisposable
{
    private readonly Image _target;
    private readonly DispatcherQueue _dispatcher;
    private SilentVideoPlayer? _player;
    private WriteableBitmap? _bitmap;
    private int _presentQueued;
    private bool _firstFrameShown;
    private bool _alphaRight;
    private byte[]? _composited;

    public SilentVideoPresenter(Image target)
    {
        _target = target;
        _dispatcher = target.DispatcherQueue;
    }

    /// <summary>The first frame is on screen (UI thread).</summary>
    public event EventHandler? FirstFrameShown;

    /// <summary>The video could not be played (UI thread).</summary>
    public event EventHandler<Exception>? Failed;

    public SilentVideoPlayer? Player => _player;

    /// <summary>Average colour of the first frame's bottom row (set before <see cref="FirstFrameShown"/>).</summary>
    public Windows.UI.Color? BottomEdgeColor { get; private set; }

    private const int LumaGrid = 32;
    private readonly float[] _luma = new float[LumaGrid * LumaGrid];
    private bool _hasLuma;

    /// <summary>Size of the decoded video in pixels (0 until opened).</summary>
    public int VideoWidth => _player?.Width ?? 0;
    public int VideoHeight => _player?.Height ?? 0;

    /// <summary>
    /// Average luminance (0–1) of the most recently shown frame inside a rectangle in normalised video
    /// coordinates, or null before the first frame. Used to keep text readable on top of the video.
    /// </summary>
    public double? Luminance(double x0, double y0, double x1, double y1)
    {
        if (!_hasLuma) return null;
        int gx0 = Math.Clamp((int)(x0 * LumaGrid), 0, LumaGrid - 1), gx1 = Math.Clamp((int)Math.Ceiling(x1 * LumaGrid) - 1, gx0, LumaGrid - 1);
        int gy0 = Math.Clamp((int)(y0 * LumaGrid), 0, LumaGrid - 1), gy1 = Math.Clamp((int)Math.Ceiling(y1 * LumaGrid) - 1, gy0, LumaGrid - 1);
        double sum = 0;
        for (int y = gy0; y <= gy1; y++)
            for (int x = gx0; x <= gx1; x++) sum += _luma[y * LumaGrid + x];
        return sum / ((gx1 - gx0 + 1) * (gy1 - gy0 + 1));
    }

    /// <summary>Samples a coarse luminance grid of the frame (one pixel per cell, Rec. 709 weights).</summary>
    private void SampleLuma(byte[] bgra, int width, int height)
    {
        for (int gy = 0; gy < LumaGrid; gy++)
        {
            int y = Math.Min(height - 1, (int)((gy + 0.5) * height / LumaGrid));
            for (int gx = 0; gx < LumaGrid; gx++)
            {
                int x = Math.Min(width - 1, (int)((gx + 0.5) * width / LumaGrid));
                int i = (y * width + x) * 4;
                _luma[gy * LumaGrid + gx] = (0.0722f * bgra[i] + 0.7152f * bgra[i + 1] + 0.2126f * bgra[i + 2]) / 255f;
            }
        }
        _hasLuma = true;
    }

    /// <param name="maxHeight">Decode height in physical pixels (the size it is shown at), or null for the full size.</param>
    public void Play(string path, VideoSegment segment, int? maxHeight = null, bool alphaRight = false)
    {
        Stop();
        _alphaRight = alphaRight;
        var player = new SilentVideoPlayer(path, segment, maxHeight);
        _player = player;
        player.Opened += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            if (_player != player) return;
            if (_alphaRight && (player.Width & 1) != 0) { Failed?.Invoke(this, new InvalidDataException("Invalid split-alpha video width.")); Stop(); return; }
            int width = _alphaRight ? player.Width / 2 : player.Width;
            _bitmap = new WriteableBitmap(width, player.Height);
            _composited = _alphaRight ? new byte[width * player.Height * 4] : null;
            _target.Source = _bitmap;
        });
        player.FrameAvailable += (_, _) =>
        {
            if (Interlocked.Exchange(ref _presentQueued, 1) == 0 && !_dispatcher.TryEnqueue(() => Present(player)))
            {
                _presentQueued = 0;
            }
        };
        player.Failed += (_, ex) => _dispatcher.TryEnqueue(() =>
        {
            if (_player == player) Failed?.Invoke(this, ex);
        });
        player.Start();
    }

    public void Pause() => _player?.Pause();

    public void Resume() => _player?.Resume();

    /// <summary>Stops decoding and releases the decoder; the last frame stays in the image.</summary>
    public void Stop()
    {
        var player = _player;
        _player = null;
        player?.Dispose();
        _firstFrameShown = false;
        _hasLuma = false;
    }

    /// <summary>Stops and clears the image.</summary>
    public void Dispose()
    {
        Stop();
        _target.Source = null;
        _bitmap = null;
    }

    private static Windows.UI.Color AverageRow(byte[] bgra, int width, int row)
    {
        long b = 0, g = 0, r = 0;
        int start = row * width * 4;
        for (int x = 0; x < width; x++)
        {
            b += bgra[start + x * 4];
            g += bgra[start + x * 4 + 1];
            r += bgra[start + x * 4 + 2];
        }
        return Windows.UI.Color.FromArgb(255, (byte)(r / width), (byte)(g / width), (byte)(b / width));
    }

    private void Present(SilentVideoPlayer player)
    {
        _presentQueued = 0;
        if (_player != player || _bitmap is not { } bitmap || !player.TryTakeFrame(out byte[] frame)) return;
        try
        {
            int length = player.Width * player.Height * 4;
            int width = _alphaRight ? player.Width / 2 : player.Width;
            if (bitmap.PixelWidth != width || bitmap.PixelHeight != player.Height || frame.Length < length) return;
            byte[] pixels = frame;
            if (_alphaRight)
            {
                if (_composited is null) return;
                SplitAlphaFrame.Compose(frame, _composited, player.Width, player.Height);
                pixels = _composited;
                length = width * player.Height * 4;
            }
            pixels.CopyTo(0, bitmap.PixelBuffer, 0, length);
            bitmap.Invalidate();
            SampleLuma(pixels, width, player.Height);
            if (!_firstFrameShown) BottomEdgeColor = AverageRow(pixels, width, player.Height - 1);
        }
        finally
        {
            player.ReturnFrame(frame);
        }
        if (!_firstFrameShown)
        {
            _firstFrameShown = true;
            FirstFrameShown?.Invoke(this, EventArgs.Empty);
        }
    }
}
