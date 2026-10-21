// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.Media;

/// <summary>Looping WebP timing with damped horizontal scrubbing and idle playback recovery.</summary>
public sealed class TurntableMotion(IReadOnlyList<int> durations)
{
    private readonly double[] _seconds = durations.Select(ms => Math.Clamp(ms, 10, 10_000) / 1000.0).ToArray();
    private double _position;
    private double _anchor;
    private double _target;
    private double _idle;
    private bool _controlled;
    public bool IsDragging { get; private set; }
    public int Frame => _seconds.Length == 0 ? 0 : (int)Wrap(_position);

    private double Wrap(double position) => _seconds.Length == 0 ? 0 : (position % _seconds.Length + _seconds.Length) % _seconds.Length;

    public void BeginDrag()
    {
        IsDragging = _controlled = true;
        _position = _anchor = _target = Wrap(_position);
        _idle = 0;
    }

    public void DragTo(double distance, double width, bool animate = true)
    {
        if (!IsDragging || !double.IsFinite(distance) || !double.IsFinite(width)) return;
        _target = _anchor + distance * _seconds.Length / Math.Max(1, width * 1.6);
        if (!animate) _position = _target;
        _idle = 0;
    }

    public void EndDrag() { IsDragging = false; _idle = 0; }

    public void Step(int frames)
    {
        BeginDrag();
        _position = _target = _position + frames;
        EndDrag();
    }

    public void Advance(double elapsedSeconds, bool autoplay = true)
    {
        if (_seconds.Length == 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        double dt = Math.Min(elapsedSeconds, 0.1); // Do not catch up after suspension or a blocked UI thread.
        if (_controlled)
        {
            _position += (_target - _position) * (1 - Math.Exp(-7.2 * dt));
            if (IsDragging) return;
            _idle += dt;
            if (_idle < 3 || Math.Abs(_target - _position) > 0.01) return;
            _controlled = false;
        }
        if (!autoplay) return;
        _position = Wrap(_position);
        while (dt > 0)
        {
            int index = Frame;
            double remainder = (1 - (_position - Math.Floor(_position))) * _seconds[index];
            if (dt < remainder) { _position += dt / _seconds[index]; break; }
            dt -= remainder;
            _position = Wrap(Math.Floor(_position) + 1);
        }
    }
}
