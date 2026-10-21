// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;

namespace EncoDeck.Platform.Media;

/// <summary>
/// Plays a <see cref="VideoSegment"/> of a video file silently: a background thread decodes with
/// <see cref="SilentVideoReader"/> (video stream only, never audio) and publishes each frame at its
/// time. The UI takes the newest frame with <see cref="TryTakeFrame"/> and gives the buffer back with
/// <see cref="ReturnFrame"/>; three buffers are reused for the whole playback.
/// <para>
/// Nothing here registers as media playback: no audio session, no System Media Transport Controls
/// entry, nothing media keys or earbud taps can pause.
/// </para>
/// </summary>
public sealed class SilentVideoPlayer : IDisposable
{
    private readonly string _path;
    private readonly VideoSegment _segment;
    private readonly int? _maxHeight;
    private readonly Lock _lock = new();
    private readonly Stack<byte[]> _free = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _resumed = new(true);
    private readonly Stopwatch _clock = new();
    private Thread? _thread;
    private byte[]? _pending;
    private volatile bool _disposed;
    private TimeSpan _pausedAt;
    private TimeSpan _pausedTotal;

    /// <param name="maxHeight">Optional decode height (see <see cref="SilentVideoReader.Open"/>).</param>
    public SilentVideoPlayer(string path, VideoSegment segment, int? maxHeight = null)
    {
        _path = path;
        _segment = segment;
        _maxHeight = maxHeight;
    }

    /// <summary>The decoder is ready; <see cref="Width"/> and <see cref="Height"/> are known. Raised on the decode thread.</summary>
    public event EventHandler? Opened;

    /// <summary>A new frame can be taken with <see cref="TryTakeFrame"/>. Raised on the decode thread.</summary>
    public event EventHandler? FrameAvailable;

    /// <summary>The video could not be opened or decoded. Raised on the decode thread.</summary>
    public event EventHandler<Exception>? Failed;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string? Pipeline { get; private set; }

    /// <summary>Media time of the newest published frame.</summary>
    public TimeSpan? LastFrameTime { get; private set; }

    /// <summary>Frames published so far (diagnostics).</summary>
    public int FramesPublished { get; private set; }

    /// <summary>True once the segment has ended and the last frame is held.</summary>
    public bool IsHolding { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "EncoDeck silent video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Stops decoding (for example while hidden); the clock stops with it.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (!_resumed.IsSet) return;
            _pausedAt = _clock.Elapsed;
            _resumed.Reset();
        }
        _wake.Set();
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_resumed.IsSet) return;
            _pausedTotal += _clock.Elapsed - _pausedAt;
            _resumed.Set();
        }
        _wake.Set();
    }

    /// <summary>The newest frame (BGRA, <see cref="Width"/> × <see cref="Height"/>), if one arrived since the last call.</summary>
    public bool TryTakeFrame(out byte[] frame)
    {
        lock (_lock)
        {
            frame = _pending!;
            _pending = null;
            return frame is not null;
        }
    }

    /// <summary>Gives a buffer from <see cref="TryTakeFrame"/> back for reuse.</summary>
    public void ReturnFrame(byte[] frame)
    {
        lock (_lock)
        {
            if (frame.Length == Width * Height * 4 && _free.Count < 3) _free.Push(frame);
        }
    }

    /// <summary>Stops the decode thread; it releases the reader (and the Media Foundation objects) itself.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resumed.Set();
        _wake.Set();
        if (_thread is null) Cleanup();
    }

    private void Cleanup()
    {
        lock (_lock)
        {
            _pending = null;
            _free.Clear();
        }
        _wake.Dispose();
        _resumed.Dispose();
    }

    /// <summary>Wall time on the playback clock (pauses excluded).</summary>
    private TimeSpan Now
    {
        get
        {
            lock (_lock) return _clock.Elapsed - _pausedTotal - (_resumed.IsSet ? TimeSpan.Zero : _clock.Elapsed - _pausedAt);
        }
    }

    private void Run()
    {
        try
        {
            using var reader = SilentVideoReader.Open(_path, _maxHeight);
            Width = reader.Width;
            Height = reader.Height;
            Pipeline = reader.Pipeline;
            lock (_lock)
            {
                for (int i = 0; i < 3; i++) _free.Push(new byte[reader.FrameBytes]);
            }
            if (_disposed) return;
            Opened?.Invoke(this, EventArgs.Empty);
            Play(reader);
        }
        catch (Exception ex) when (!_disposed)
        {
            Failed?.Invoke(this, ex);
        }
        catch
        {
            // Disposed while opening or decoding: nothing to report.
        }
        finally
        {
            if (_disposed) Cleanup();
        }
    }

    private void Play(SilentVideoReader reader)
    {
        _clock.Start();
        var target = _segment.Start;
        if (target > TimeSpan.Zero && !reader.TrySeek(target))
        {
            // The segment starts after the end of this video: nothing to show.
            IsHolding = true;
            return;
        }
        TimeSpan? anchorFrame = null;
        var anchorWall = TimeSpan.Zero;
        var lastDue = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(1.0 / 30);
        TimeSpan? previousFrame = null;

        while (!_disposed)
        {
            if (!WaitWhilePaused()) return;
            byte[]? buffer = RentBuffer();
            if (buffer is null) return;
            bool decoded = reader.ReadFrame(buffer, out var time);
            var action = decoded ? _segment.Classify(time, target) : VideoFrameAction.PastEnd;
            if (action == VideoFrameAction.Skip)
            {
                ReturnFrame(buffer);
                continue;
            }
            if (action == VideoFrameAction.PastEnd)
            {
                ReturnFrame(buffer);
                if (_segment.ContinueAfterEnd() is not { } loop)
                {
                    // Hold the last frame: nothing more to decode.
                    IsHolding = true;
                    return;
                }
                // Continue from the loop point one frame after the last one shown.
                target = loop;
                if (!reader.TrySeek(loop))
                {
                    IsHolding = true;
                    return;
                }
                anchorFrame = null;
                anchorWall = lastDue + interval;
                previousFrame = null;
                continue;
            }

            if (previousFrame is { } prev && time > prev && time - prev < TimeSpan.FromSeconds(0.5)) interval = time - prev;
            previousFrame = time;
            if (anchorFrame is null)
            {
                anchorFrame = time;
                if (anchorWall < Now) anchorWall = Now;
            }
            var due = VideoSegment.DueAt(time, anchorFrame.Value, anchorWall);
            if (!WaitUntil(due))
            {
                ReturnFrame(buffer);
                return;
            }
            lastDue = due;
            Publish(buffer, time);
        }
    }

    private byte[]? RentBuffer()
    {
        lock (_lock)
        {
            if (_free.Count > 0) return _free.Pop();
            // The UI holds two frames: drop the pending one rather than allocate.
            if (_pending is { } stale)
            {
                _pending = null;
                return stale;
            }
        }
        return _disposed ? null : new byte[Width * Height * 4];
    }

    private void Publish(byte[] frame, TimeSpan time)
    {
        lock (_lock)
        {
            if (_pending is { } replaced && _free.Count < 3) _free.Push(replaced);
            _pending = frame;
        }
        LastFrameTime = time;
        FramesPublished++;
        FrameAvailable?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>False when disposed.</returns>
    private bool WaitWhilePaused()
    {
        while (!_resumed.IsSet && !_disposed) _resumed.Wait(250);
        return !_disposed;
    }

    /// <summary>Sleeps until the playback clock reaches <paramref name="due"/> (pauses stop the clock).</summary>
    private bool WaitUntil(TimeSpan due)
    {
        while (!_disposed)
        {
            if (!WaitWhilePaused()) return false;
            var wait = due - Now;
            if (wait <= TimeSpan.FromMilliseconds(1)) return true;
            _wake.WaitOne(wait);
        }
        return false;
    }
}
