// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace EncoDeck.Platform.Resources;

/// <summary>
/// How the stand-in pictures (and the foreground animation) enter after <c>resShowDelay</c>
/// (HeyMelody <c>resInAnim</c>, <c>DiscoveryAnimUtils</c>): a 300 ms fade, a 1 s slide in from the
/// right edge, or a 300 ms zoom from 80 % with a fade. The battery levels themselves always fade.
/// </summary>
public enum PopupEntrance
{
    FadeIn,
    RightSlideIn,
    ZoomIn,
}

/// <summary>
/// The official pop-up timeline for one HeyMelody pack (<c>discoveryStates</c> in its <c>config.json</c>):
/// which video segment to play, when the battery levels, the title and the pictures appear and when
/// the pop-up closes (HeyMelody <c>DiscoveryViewStateConnectedImpl</c> / <c>DiscoveryDialogActivity</c>).
/// Where things go is <see cref="PopupLayout"/>.
/// </summary>
public sealed record PopupTimeline(
    TimeSpan VideoStart,
    TimeSpan? VideoEnd,
    TimeSpan? LoopFrom,
    TimeSpan CloseDelay,
    TimeSpan InfoDelay,
    PopupEntrance Entrance,
    bool ShowTitle,
    string? TextColor)
{
    /// <summary>When the title fades in (<c>titleFadeInDelay</c>, else <c>infoFadeInDelay</c>).</summary>
    public TimeSpan TitleDelay { get; init; }

    /// <summary>When the stand-in pictures enter with <see cref="Entrance"/> (<c>resShowDelay</c>).</summary>
    public TimeSpan ImageDelay { get; init; }

    /// <summary>HeyMelody defaults when a pack sets nothing (connected state closes after 5000 ms).</summary>
    public static PopupTimeline Default { get; } = new(TimeSpan.Zero, null, null, TimeSpan.FromMilliseconds(5000), TimeSpan.Zero, PopupEntrance.FadeIn, true, null);

    private static readonly TimeSpan MinClose = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxClose = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Finds the pack's <c>config.json</c> next to or above the pop-up video (HeyMelody keeps the video
    /// under <c>res/video…/</c>) and reads it. Null when the media has no official config.
    /// </summary>
    public static PopupTimeline? TryReadFor(string mediaPath, int maxLevels = 4) =>
        FindConfigFor(mediaPath, maxLevels) is { } config ? Parse(File.ReadAllText(config)) : null;

    /// <summary>The <c>config.json</c> with pop-up states next to or above <paramref name="mediaPath"/>, or null.</summary>
    public static string? FindConfigFor(string mediaPath, int maxLevels = 4)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(mediaPath));
        for (int level = 0; dir is not null && level <= maxLevels; level++)
        {
            string config = Path.Combine(dir, "config.json");
            try
            {
                if (File.Exists(config) && Parse(File.ReadAllText(config)) is not null) return config;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Reads <c>backConnectedState</c> (what HeyMelody shows when a known device reconnects, which is
    /// always the case on a PC), else <c>connectedState</c>. Null if the JSON has no pop-up states.
    /// <para>
    /// <see cref="InfoDelay"/> is the battery levels' delay: <c>batteryFadeInDelay</c>, else
    /// <c>infoFadeInDelay</c>. <see cref="TextColor"/> is the chosen state's own
    /// <c>batteriesRes.textColor</c> (HeyMelody never borrows another state's colours).
    /// </para>
    /// </summary>
    public static PopupTimeline? Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            if (SelectState(doc.RootElement) is not { } state) return null;
            string? textColor = BatteryTextColor(state);

            var d = Default;
            var start = Millis(state, "startTime") ?? TimeSpan.Zero;
            var end = Millis(state, "endTime");
            var loop = Millis(state, "loopTime");
            var close = Clamp(Positive(state, "closeDelay") ?? d.CloseDelay, MinClose, MaxClose);
            // A delay of 0 (or none) means at once; the battery and title delays default to the info one.
            var infoFadeIn = Positive(state, "infoFadeInDelay") ?? d.InfoDelay;
            var info = Clamp(Positive(state, "batteryFadeInDelay") ?? infoFadeIn, TimeSpan.Zero, close);
            var titleDelay = Clamp(Positive(state, "titleFadeInDelay") ?? infoFadeIn, TimeSpan.Zero, close);
            var imageDelay = Clamp(Positive(state, "resShowDelay") ?? TimeSpan.Zero, TimeSpan.Zero, close);
            if (end is { } e && e <= start) end = null;
            if (loop is { } l && (l < start || end is null || l >= end)) loop = null;

            string? anim = state.TryGetProperty("resInAnim", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var entrance = anim switch
            {
                "rightSlideIn" => PopupEntrance.RightSlideIn,
                "zoomIn" => PopupEntrance.ZoomIn,
                _ => PopupEntrance.FadeIn,
            };
            bool showTitle = !state.TryGetProperty("showTitleRes", out var title) || title.ValueKind != JsonValueKind.False;
            return new PopupTimeline(start, end, loop, close, info, entrance, showTitle, textColor) { TitleDelay = titleDelay, ImageDelay = imageDelay };
        }
    }

    /// <summary>
    /// The pop-up state HeyMelody uses for a known device that connects (DiscoveryViewStateConnectedImpl:
    /// <c>backConnectedState</c> unless it is a new-device discovery, else <c>connectedState</c>).
    /// </summary>
    internal static JsonElement? SelectState(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("discoveryStates", out var states)
            || states.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (string name in new[] { "backConnectedState", "connectedState" })
        {
            if (states.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.Object) return candidate;
        }
        return null;
    }

    private static string? BatteryTextColor(JsonElement state) =>
        state.ValueKind == JsonValueKind.Object
        && state.TryGetProperty("batteriesRes", out var batteries) && batteries.ValueKind == JsonValueKind.Object
        && batteries.TryGetProperty("textColor", out var color) && color.ValueKind == JsonValueKind.String
            ? color.GetString()
            : null;

    private static TimeSpan? Millis(JsonElement state, string name) =>
        state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int ms) && ms >= 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    private static TimeSpan? Positive(JsonElement state, string name) => Millis(state, name) is { } t && t > TimeSpan.Zero ? t : null;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
