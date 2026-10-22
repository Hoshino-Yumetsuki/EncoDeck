// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Media;

namespace EncoDeck.Tests.Platform;

public sealed class SplitAlphaFrameTests
{
    [Fact]
    public void RightMaskProducesPremultipliedBgraWithoutLeakingTheMaskHalf()
    {
        byte[] source = [20, 40, 80, 255, 100, 200, 50, 255, 255, 255, 255, 255, 128, 128, 128, 255];
        byte[] result = new byte[8];
        SplitAlphaFrame.Compose(source, result, 4, 1);
        Assert.Equal(new byte[] { 20, 40, 80, 255, 50, 100, 25, 128 }, result);
        source[14] = 0;
        SplitAlphaFrame.Compose(source, result, 4, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, result[4..]);
    }

    [Fact]
    public void OddWidthAndTruncatedFramesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SplitAlphaFrame.Compose(new byte[16], new byte[8], 3, 1));
        Assert.Throws<ArgumentException>(() => SplitAlphaFrame.Compose(new byte[15], new byte[8], 4, 1));
        Assert.Throws<ArgumentException>(() => SplitAlphaFrame.Compose(new byte[16], new byte[7], 4, 1));
    }
}
