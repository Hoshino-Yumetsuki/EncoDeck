using EncoDeck.Core.Resources;

namespace EncoDeck.Tests;

public sealed class HuaweiArtworkPolicyTests
{
    [Theory]
    [InlineData("animation.alpha-right.mp4", false)]
    [InlineData("ear_connected_success_overlap.mp4", false)]
    [InlineData("animation.reconnect.alpha-right.mp4", true)]
    [InlineData("animationDark.reconnect.mp4", true)]
    public void DownloadsMustIdentifyVerifiedReconnectResources(string path, bool accepted)
    {
        var file = new DeviceResourceFile(path, DeviceResourceKind.Video, DeviceResourceOrigin.Downloaded, null);
        Assert.Equal(accepted, HuaweiArtworkPolicy.ForRole(file, DeviceResourceRole.PopupAnimation) is not null);
    }

    [Fact]
    public void OldConnectionLoopCannotBeUsedAsDevicePageVideo()
    {
        var file = new DeviceResourceFile("motion.alpha-right.mp4", DeviceResourceKind.Video, DeviceResourceOrigin.Downloaded, null);
        Assert.Null(HuaweiArtworkPolicy.ForRole(file, DeviceResourceRole.DeviceAnimation));
        Assert.NotNull(HuaweiArtworkPolicy.ForRole(file with { Path = "motion.device-once.mp4" }, DeviceResourceRole.DeviceAnimation));
        var custom = file with { Origin = DeviceResourceOrigin.Custom };
        Assert.Same(custom, HuaweiArtworkPolicy.ForRole(custom, DeviceResourceRole.DeviceAnimation));
    }
}
