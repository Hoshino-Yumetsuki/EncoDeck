// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Spatial audio as offered by the official client: off, fixed, or following the head.</summary>
public enum SpatialAudioMode
{
    Off = 0,
    Fixed = 1,
    HeadTracking = 2,
}

/// <summary>Game sound enhancement: the selected type and the types the earbuds support (0 = off).</summary>
public sealed record GameSoundState(int SelectedType, IReadOnlyList<int> SupportedTypes);

/// <summary>
/// The earbuds' LE/BR switch (the official "game low latency with LE Audio" setting). Answering
/// it means the firmware can run LE Audio; <see cref="Owner"/> and <see cref="ResetFlag"/> are raw bits.
/// </summary>
public sealed record LeAudioSwitchState(bool Enabled, bool Owner, bool ResetFlag);

/// <summary>Bass engine (BassWave) level with the range the earbuds report.</summary>
public sealed record BassEngineState(int Min, int Max, int Current);

public enum EarSide
{
    Left = 1,
    Right = 2,
    /// <summary>Records that apply to both earbuds (call controls).</summary>
    Both = 4,
}

/// <summary>Gesture action as encoded on the wire.</summary>
public enum GestureAction
{
    SingleSqueeze = 1,
    DoubleSqueeze = 2,
    TripleSqueeze = 3,
    SqueezeAndHold = 4,
    Slide = 5,
    /// <summary>Extra-long press (touch or squeeze and hold for several seconds).</summary>
    LongHold = 6,
}

/// <summary>One gesture assignment: <c>{devType, button, action, function}</c>.</summary>
public sealed record KeyFunction(EarSide Side, int Button, GestureAction Action, int Function)
{
    /// <summary>Button id used for in-call gestures.</summary>
    public const int CallButton = 6;

    public bool IsCallControl => Button == CallButton;
}

/// <summary>Modes the long-press (squeeze and hold) noise-control cycle steps through, per earbud.</summary>
public sealed record NoiseCycleState(uint LeftMask, uint RightMask);

/// <summary>Result of the earbud fit test for one side.</summary>
public enum FitQuality
{
    Unknown,
    Good,
    Average,
    Poor,
    Failed,
}

public sealed record FitTestResult(FitQuality Left, FitQuality Right);

/// <summary>A phone/PC/tablet the earbuds know (dual connection). The address is in display order.</summary>
public sealed record SourceDevice(byte[] Address, string Name, int State, bool IsThisDevice, bool IsAudioActive, int DeviceType)
{
    public bool IsConnected => State == 2;

    public string AddressText => string.Join(':', Address.Select(b => b.ToString("X2")));
}

/// <summary>Dual-connection preferred device: null address means "auto select".</summary>
public sealed record PreferredDeviceState(byte[]? Address);

/// <summary>A user-defined device-side EQ preset.</summary>
public sealed record CustomEqualizer(int Id, string Name, int MinGain, int MaxGain, bool IsSelected, IReadOnlyList<EqualizerBand> Bands);

public sealed record EqualizerBand(int FrequencyHz, int Gain);
