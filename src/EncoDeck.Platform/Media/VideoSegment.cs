// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Platform.Media;

/// <summary>What the player does with a decoded frame.</summary>
public enum VideoFrameAction
{
    /// <summary>Decoded on the way to the seek target (from the previous key frame): not shown.</summary>
    Skip,
    /// <summary>Inside the segment: shown at its time.</summary>
    Present,
    /// <summary>Past the end of the segment: not shown; the segment has ended.</summary>
    PastEnd,
}

/// <summary>
/// The part of a video to play, as HeyMelody's connection pop-up does with a pack's
/// <c>startTime</c> / <c>endTime</c> / <c>loopTime</c>: play <c>[Start, End]</c>, then hold the
/// last frame, or continue from <see cref="LoopFrom"/> (to <see cref="End"/> again) for as long as
/// it is shown. No end means the end of the stream.
/// </summary>
public sealed record VideoSegment(TimeSpan Start, TimeSpan? End = null, TimeSpan? LoopFrom = null)
{
    /// <summary>Frames this close to a boundary count as on it (about half a frame at 60 fps).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(8);

    /// <summary>The whole video, then hold the last frame.</summary>
    public static VideoSegment Whole { get; } = new(TimeSpan.Zero);

    /// <summary>The whole video, over and over.</summary>
    public static VideoSegment WholeLooping { get; } = new(TimeSpan.Zero, null, TimeSpan.Zero);

    public bool Loops => LoopFrom is not null;

    /// <summary>
    /// Classifies a decoded frame. <paramref name="seekTarget"/> is where the last seek aimed (the
    /// start, or the loop point): the reader resumes at the key frame before it, and those frames
    /// are decoded but not shown.
    /// </summary>
    public VideoFrameAction Classify(TimeSpan frameTime, TimeSpan seekTarget)
    {
        if (frameTime + Tolerance < seekTarget) return VideoFrameAction.Skip;
        if (End is { } end && frameTime > end + Tolerance) return VideoFrameAction.PastEnd;
        return VideoFrameAction.Present;
    }

    /// <summary>
    /// Where to continue once the segment has ended (a frame past <see cref="End"/> or the end of the
    /// stream): the loop point, or null to hold the last frame shown.
    /// </summary>
    public TimeSpan? ContinueAfterEnd() => LoopFrom;

    /// <summary>
    /// When a frame is due on the wall clock: frames keep their spacing from the frame the clock was
    /// anchored to (the first one after a start, seek or loop).
    /// </summary>
    public static TimeSpan DueAt(TimeSpan frameTime, TimeSpan anchorFrameTime, TimeSpan anchorWallTime)
    {
        var due = anchorWallTime + (frameTime - anchorFrameTime);
        return due < anchorWallTime ? anchorWallTime : due;
    }
}
