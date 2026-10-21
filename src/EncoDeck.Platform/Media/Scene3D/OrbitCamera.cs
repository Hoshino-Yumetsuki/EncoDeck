// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>
/// The camera of HeyMelody's 3D detail model (<c>com.oplusos.vfxmodelviewer.view.CameraController</c>),
/// with the same states and constants: it orbits the model centre at (0, 0, -4) from (0, 0, 1); a drag
/// turns it freely around the vertical axis and tilts it up to ±85°, both with damped inertia; pinch
/// (here: the mouse wheel) zooms between 0.75× and 1.5× the start distance. Without input it turns
/// at 20°/s; three seconds after the last input it coasts out, then over ten seconds eases back to
/// level, the start distance and the idle speed.
/// <para>
/// Drag distances are in HeyMelody's touch pixels per 1/60 s (see <see cref="TouchPixelsPerDip"/>).
/// Angles are degrees.
/// </para>
/// </summary>
public sealed class OrbitCamera
{
    public enum State { Idle, Control, ControlOver, Rotate, Recover, ControlOverToRotate }

    public static readonly Vector3 Center = new(0, 0, -4);
    public static readonly Vector3 StartEye = new(0, 0, 1);
    public const float VerticalFieldOfView = 45;
    public const float Near = 0.5f;
    public const float Far = 100;
    /// <summary>A phone's touch pixels per DIP, so a drag turns the model as far as on HeyMelody.</summary>
    public const float TouchPixelsPerDip = 3;

    private const float SlideSpeed = 21;
    private const float Damp = 7.2f;
    private const float MaxPitch = 85;
    private const float MinDistancePercent = 0.75f;
    private const float MaxDistancePercent = 1.5f;
    private const float RecoverSeconds = 1;
    private const float ReturnSeconds = 10;
    private const float ControlTimeout = 3;

    private Vector2 _pendingSlide;
    private float _pendingScroll;
    private Vector2 _targetSlide;
    private Vector2 _currentSlide;
    private float _targetDistance;
    private float _noInput;
    private float _stateTime;
    private float _speed;
    private float _recoverYaw, _recoverPitch, _recoverDistance;

    public OrbitCamera()
    {
        DefaultDistance = Vector3.Distance(Center, StartEye);
        Distance = _targetDistance = DefaultDistance;
        SwitchToRotate();
    }

    /// <summary>Idle turning speed (HeyMelody: 20°/s).</summary>
    public float RotateSpeed { get; set; } = 20;

    /// <summary>False when the system asks for reduced motion: no idle turning (input still works).</summary>
    public bool AutoRotate { get; set; } = true;

    public State Current { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Distance { get; private set; }
    public float DefaultDistance { get; }
    public float MinDistance => DefaultDistance * MinDistancePercent;
    public float MaxDistance => DefaultDistance * MaxDistancePercent;

    /// <summary>True while the view changes and should be redrawn.</summary>
    public bool IsMoving => Current switch
    {
        State.Rotate or State.ControlOverToRotate => AutoRotate || Current == State.ControlOverToRotate,
        State.Idle => false,
        _ => true,
    };

    public Vector3 Eye
    {
        get
        {
            var rotation = Rotation;
            return Center + Vector3.Transform(new Vector3(0, 0, Distance), rotation);
        }
    }

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>Unity / HeyMelody Euler order: yaw (Y) after pitch (X).</summary>
    private Quaternion Rotation =>
        Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(Pitch)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(Yaw)));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Center, Up);

    public static Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(Radians(VerticalFieldOfView), Math.Max(aspect, 0.01f), Near, Far);

    /// <summary>
    /// HeyMelody's <c>ModelScene.updateScale</c>: scales the model so its largest half extent is
    /// <paramref name="modelScale"/> and moves its bounding-box centre to <see cref="Center"/>.
    /// </summary>
    public static Matrix4x4 ModelTransform(Vector3 boundsCenter, Vector3 halfExtent, float modelScale)
    {
        float largest = Math.Max(halfExtent.X, Math.Max(halfExtent.Y, halfExtent.Z));
        float scale = largest > 0 ? modelScale / largest : 1;
        return Matrix4x4.CreateTranslation(-boundsCenter) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(Center);
    }

    /// <summary>A still camera at the given angles and the start distance (diagnostics, tests).</summary>
    public static OrbitCamera ForView(float yaw, float pitch)
    {
        var camera = new OrbitCamera { AutoRotate = false };
        camera.ResetImmediately();
        camera.Yaw = Wrap(yaw);
        camera.Pitch = Math.Clamp(pitch, -MaxPitch, MaxPitch);
        return camera;
    }

    // ---- input ----------------------------------------------------------------------------------

    /// <summary>A finger (or the mouse button) went down.</summary>
    public void BeginControl()
    {
        if (Current != State.Control)
        {
            _targetDistance = Distance;
            _targetSlide = Vector2.Zero;
            _currentSlide = Vector2.Zero;
            Current = State.Control;
        }
        _pendingSlide = Vector2.Zero;
        _noInput = 0;
    }

    /// <summary>Pointer movement in touch pixels since the previous call.</summary>
    public void Slide(float dx, float dy)
    {
        if (Current != State.Control) BeginControl();
        _pendingSlide += new Vector2(dx, dy);
        _noInput = 0;
    }

    /// <summary>Pinch distance change in touch pixels (positive = fingers apart = closer).</summary>
    public void Scroll(float amount)
    {
        if (Current != State.Control) BeginControl();
        _pendingScroll += amount;
        _noInput = 0;
    }

    /// <summary>The finger (or button) was released; the wheel calls this right after scrolling.</summary>
    public void EndControl()
    {
        if (Current != State.Control) return;
        // Input not yet consumed by a frame (a wheel notch, an arrow key) still counts.
        _targetDistance = Math.Clamp(_targetDistance + _pendingScroll * -0.6f / 60, MinDistance, MaxDistance);
        _currentSlide += _pendingSlide;
        _pendingScroll = 0;
        _pendingSlide = Vector2.Zero;
        Current = State.ControlOver;
        _targetSlide = Vector2.Zero;
        _stateTime = 0;
    }

    /// <summary>Animates back to the start view, then resumes turning.</summary>
    public void StartRecover()
    {
        Current = State.Recover;
        _recoverYaw = Yaw;
        _recoverPitch = Pitch;
        _recoverDistance = Distance;
        _stateTime = 0;
    }

    public void ResetImmediately()
    {
        Yaw = Pitch = 0;
        Distance = _targetDistance = DefaultDistance;
        _currentSlide = _targetSlide = Vector2.Zero;
        Current = State.Idle;
        _stateTime = 0;
    }

    // ---- per frame ------------------------------------------------------------------------------

    public void Update(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, 0.1f); // no catching up after a stall
        switch (Current)
        {
            case State.Recover: UpdateRecover(dt); break;
            case State.Control: UpdateControl(dt); break;
            case State.Idle:
                _stateTime += dt;
                if (_stateTime >= 0.5f) SwitchToRotate();
                break;
            case State.Rotate: UpdateRotate(dt); break;
            case State.ControlOver: UpdateControlOver(dt); break;
            case State.ControlOverToRotate: UpdateControlOverToRotate(dt); break;
        }
    }

    private void UpdateControl(float dt)
    {
        // HeyMelody keeps the last touch delta per event (~60 Hz); express the movement per 1/60 s.
        _targetSlide = _pendingSlide / (dt * 60);
        _pendingSlide = Vector2.Zero;
        // HeyMelody: distance += pinch delta per event * -0.6 * dt, i.e. -0.01 per pixel at 60 Hz.
        _targetDistance = Math.Clamp(_targetDistance + _pendingScroll * -0.6f / 60, MinDistance, MaxDistance);
        _pendingScroll = 0;
        UpdateCameraMatrix(dt);
        _noInput += dt;
        if (_noInput >= ControlTimeout) EndControl();
    }

    private void UpdateCameraMatrix(float dt)
    {
        float k = Math.Min(1, Damp * dt);
        float step = -SlideSpeed * dt;
        _currentSlide = Vector2.Lerp(_currentSlide, _targetSlide, k);
        Yaw = Wrap(Yaw + _currentSlide.X * step);
        Pitch = Math.Clamp(Pitch + _currentSlide.Y * step, -MaxPitch, MaxPitch);
        Distance += (_targetDistance - Distance) * k;
    }

    private void UpdateControlOver(float dt)
    {
        UpdateCameraMatrix(dt);
        _stateTime += dt;
        if (Math.Abs(Distance - _targetDistance) > 0.01f || _currentSlide.LengthSquared() > 0.01f) return;
        Current = State.ControlOverToRotate;
        _speed = RotateSpeed * 2;
        _stateTime = 0;
    }

    private void UpdateControlOverToRotate(float dt)
    {
        _stateTime += dt;
        float k = Math.Clamp(_stateTime / ReturnSeconds, 0, 1);
        Pitch = Lerp(Pitch, 0, k);
        Distance = _targetDistance = Lerp(Distance, DefaultDistance, k);
        _speed = Lerp(_speed, RotateSpeed, k);
        UpdateRotate(dt);
        if (k >= 1) SwitchToRotate();
    }

    private void UpdateRotate(float dt)
    {
        if (AutoRotate || Current == State.ControlOverToRotate) Yaw = Wrap(Yaw + _speed * dt);
    }

    private void UpdateRecover(float dt)
    {
        _stateTime += dt;
        float t = RecoverSeconds > 0 ? Math.Clamp(_stateTime / RecoverSeconds, 0, 1) : 1;
        float e = EaseInOutCubic(t);
        float yawDelta = Wrap(0 - _recoverYaw);
        Yaw = Wrap(_recoverYaw + yawDelta * e);
        Pitch = Lerp(_recoverPitch, 0, e);
        Distance = _targetDistance = Lerp(_recoverDistance, DefaultDistance, e);
        if (e >= 1) SwitchToRotate();
    }

    private void SwitchToRotate()
    {
        Current = State.Rotate;
        _speed = RotateSpeed;
    }

    private static float EaseInOutCubic(float t) => t < 0.5f ? 4 * t * t * t : 1 + (float)Math.Pow(2 * t - 2, 3) / 2;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Wrap(float degrees) => ((degrees + 180) % 360 + 360) % 360 - 180;

    private static float Radians(float degrees) => degrees * MathF.PI / 180;
}
