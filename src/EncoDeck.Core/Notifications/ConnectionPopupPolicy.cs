// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Configuration;

namespace EncoDeck.Core.Notifications;

public enum PopupDecision
{
    Show,
    /// <summary>
    /// Show, but as a click-through overlay: a borderless fullscreen app or game is in front, so
    /// the popup must never take focus or receive mouse input.
    /// </summary>
    ShowPassThrough,
    Disabled,
    /// <summary>Shown for this device recently (reconnect jitter, quick off/on).</summary>
    Cooldown,
    SuppressedFullscreen,
    SuppressedAway,
}

/// <summary>What made the earbuds worth announcing.</summary>
public enum PopupTrigger
{
    /// <summary>The Bluetooth link came up (once per connection).</summary>
    Connected,
    /// <summary>The case lid opened while connected (official second trigger).</summary>
    CaseLidOpened,
}

/// <summary>
/// Decides whether a Bluetooth connection should produce the connection popup, the way
/// HeyMelody does: once per connection, with a 5 s per-device debounce so a link that drops and
/// comes straight back does not pop up twice. Opening the case lid while connected shows it again.
/// </summary>
public sealed class ConnectionPopupPolicy
{
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Per-device debounce for the connection trigger (official: 5000 ms).</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-device debounce for the case-lid trigger (official: 5000 ms).</summary>
    public TimeSpan LidDebounce { get; init; } = TimeSpan.FromSeconds(5);

    /// <param name="isFullscreen">A fullscreen app, game or busy state is in front.</param>
    /// <param name="isExclusiveFullscreen">
    /// Exclusive (D3D) fullscreen or presentation mode: nothing is ever shown, because a topmost
    /// window can knock a game out of exclusive fullscreen.
    /// </param>
    public PopupDecision Evaluate(
        string deviceId,
        DateTimeOffset now,
        AppSettings settings,
        bool isFullscreen,
        bool isAway,
        bool isExclusiveFullscreen = false,
        PopupTrigger trigger = PopupTrigger.Connected)
    {
        if (!settings.ShowConnectionPopup) return PopupDecision.Disabled;
        if (isAway) return PopupDecision.SuppressedAway;
        if (isExclusiveFullscreen) return PopupDecision.SuppressedFullscreen;
        if (isFullscreen && settings.SuppressPopupWhenFullscreen) return PopupDecision.SuppressedFullscreen;
        var window = trigger == PopupTrigger.CaseLidOpened ? LidDebounce : Cooldown;
        lock (_gate)
        {
            if (_lastShown.TryGetValue(deviceId, out var last) && now - last < window) return PopupDecision.Cooldown;
        }
        return isFullscreen ? PopupDecision.ShowPassThrough : PopupDecision.Show;
    }

    public void RecordShown(string deviceId, DateTimeOffset now)
    {
        lock (_gate) _lastShown[deviceId] = now;
    }
}
