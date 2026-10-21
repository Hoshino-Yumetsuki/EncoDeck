// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Media;

namespace EncoDeck.Tests.Platform;

public sealed class TurntableMotionTests
{
    [Fact]
    public void PlaybackFollowsFrameTimingAndLoops()
    {
        var motion = new TurntableMotion([100, 100, 100]);
        motion.Advance(0.1); Assert.Equal(1, motion.Frame);
        motion.Advance(0.1); Assert.Equal(2, motion.Frame);
        motion.Advance(0.1); Assert.Equal(0, motion.Frame);
    }

    [Fact]
    public void DragIsDampedWrapsBothDirectionsAndResumesPlaybackAfterIdle()
    {
        var motion = new TurntableMotion(Enumerable.Repeat(33, 180).ToArray());
        motion.BeginDrag();
        motion.DragTo(400, 500);
        motion.Advance(0.016);
        Assert.InRange(motion.Frame, 1, 89);
        for (int i = 0; i < 30; i++) motion.Advance(0.1);
        Assert.InRange(motion.Frame, 89, 90);
        motion.EndDrag();
        for (int i = 0; i < 20; i++) motion.Advance(0.1);
        Assert.InRange(motion.Frame, 89, 90);
        for (int i = 0; i < 20; i++) motion.Advance(0.1);
        Assert.NotEqual(90, motion.Frame);
        motion.BeginDrag();
        motion.DragTo(-8000, 500, animate: false);
        Assert.InRange(motion.Frame, 0, 179);
    }

    [Fact]
    public void ReducedMotionDisablesPlaybackButKeepsDragAndKeyboardUsable()
    {
        var motion = new TurntableMotion(Enumerable.Repeat(33, 180).ToArray());
        motion.Advance(0.1, autoplay: false);
        Assert.Equal(0, motion.Frame);
        motion.BeginDrag(); motion.DragTo(400, 500, animate: false);
        Assert.Equal(90, motion.Frame);
        motion.EndDrag(); motion.Step(-5);
        Assert.Equal(85, motion.Frame);
        motion.Advance(double.NaN);
        Assert.Equal(85, motion.Frame);
    }
}
