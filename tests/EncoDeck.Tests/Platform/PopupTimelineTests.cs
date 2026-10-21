// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Resources;
using EncoDeck.Platform.Shell;

namespace EncoDeck.Tests.Platform;

public sealed class PopupTimelineTests : IDisposable
{
    // Same structure and values as the Enco X4 pack's pop-up states (synthetic copy).
    internal const string X4Like = """
        {"videoRes":{"path":"res/video{NIGHT}/boot.mp4"},"discoveryStates":{
          "initState":{"autoCrop":true,"startTime":0,"endTime":7400},
          "connectedState":{"closeDelay":7500,"infoFadeInDelay":0,"resShowDelay":0,"resInAnim":"rightSlideIn",
            "batteriesRes":{"textColor":"#000000"}},
          "backConnectedState":{"showTitleRes":false,"autoCrop":true,"batteryGroupStyle":3,"infoFadeInDelay":2500,
            "resInAnim":"rightSlideIn","closeDelay":7500,"resShowDelay":2500,"startTime":0,"endTime":7500,
            "batteriesRes":{"topMargin":60,"rightMargin":60,"gravity":53}}}}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "EncoDeckTests-tl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Reconnect_UsesBackConnectedState()
    {
        var t = PopupTimeline.Parse(X4Like)!;
        Assert.Equal(TimeSpan.Zero, t.VideoStart);
        Assert.Equal(TimeSpan.FromMilliseconds(7500), t.VideoEnd);
        Assert.Null(t.LoopFrom);
        Assert.Equal(TimeSpan.FromMilliseconds(7500), t.CloseDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), t.InfoDelay);
        Assert.Equal(PopupEntrance.RightSlideIn, t.Entrance);
        Assert.False(t.ShowTitle);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), t.TitleDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), t.ImageDelay);
        // HeyMelody uses only the chosen state's batteriesRes; the reconnect state sets no colour.
        Assert.Null(t.TextColor);
    }

    [Fact]
    public void BatteryAndTitleDelays_DefaultToTheInfoDelay_AsHeyMelody()
    {
        var t = PopupTimeline.Parse("""{"discoveryStates":{"connectedState":{"infoFadeInDelay":1000,"batteryFadeInDelay":1800,"resShowDelay":300,"resInAnim":"zoomIn","closeDelay":0}}}""")!;
        Assert.Equal(TimeSpan.FromMilliseconds(1800), t.InfoDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), t.TitleDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(300), t.ImageDelay);
        Assert.Equal(PopupEntrance.ZoomIn, t.Entrance);
        // closeDelay <= 0 means HeyMelody's 5000 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(5000), t.CloseDelay);
    }

    [Fact]
    public void ProductShotPack_LoopsItsConnectedSegment()
    {
        var t = PopupTimeline.Parse(X3Like)!;
        Assert.Equal(TimeSpan.FromMilliseconds(22000), t.VideoStart);
        Assert.Equal(TimeSpan.FromMilliseconds(29930), t.VideoEnd);
        Assert.Equal(TimeSpan.FromMilliseconds(22000), t.LoopFrom);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), t.CloseDelay);
        Assert.Equal(TimeSpan.Zero, t.InfoDelay);
        Assert.True(t.ShowTitle);
    }

    // Same structure and values as the Enco X3 pack's pop-up states (synthetic copy).
    internal const string X3Like = """
        {"videoRes":{"path":"res/video{NIGHT}/boot.mp4","backgroundColor":"#fafafa","nightBackgroundColor":"#404040"},"videoLandOffsetY":20,
         "discoveryStates":{"initState":{"startTime":0,"endTime":1700},
          "readyState":{"continueTime":1600,"startTime":2270,"loopTime":2270,"endTime":9600,"imageRes":{"path":"res/image/disconnected.png"}},
          "connectedState":{"startTime":22000,"loopTime":22000,"endTime":29930,"boxImageRes":{"path":"res/image/connected_box.png"},"budsImageRes":{"path":"res/image/connected_buds.png"}}}}
        """;

    // Same structure as the Enco Free4 pack (no background colour, video fade-in).
    internal const string Free4Like = """
        {"videoRes":{"path":"res/video{NIGHT}/boot.mp4"},"videoLandOffsetY":20,
         "discoveryStates":{"connectedState":{"animation":1,"startTime":22000,"loopTime":22000,"endTime":29920,
          "boxImageRes":{"path":"res/image/connected_box.png"},"budsImageRes":{"path":"res/image/connected_buds.png"}}}}
        """;

    [Fact]
    public void ConnectedState_IsTheFallback_WithOfficialDefaults()
    {
        var t = PopupTimeline.Parse("""{"discoveryStates":{"connectedState":{"batteriesRes":{"textColor":"#FF112233"}}}}""")!;
        Assert.Equal(PopupTimeline.Default.CloseDelay, t.CloseDelay);
        Assert.Equal(TimeSpan.Zero, t.InfoDelay);
        Assert.Equal(PopupEntrance.FadeIn, t.Entrance);
        Assert.True(t.ShowTitle);
        Assert.Equal("#FF112233", t.TextColor);
        Assert.Null(t.VideoEnd);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"discoveryStates":{}}""")]
    [InlineData("""{"modelWebp":{"path":"res/raw/detail_model.webp"}}""")]
    [InlineData("not json")]
    public void NoPopupStates_IsNull(string json) => Assert.Null(PopupTimeline.Parse(json));

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var t = PopupTimeline.Parse("""{"discoveryStates":{"connectedState":{"closeDelay":999999,"infoFadeInDelay":999999,"startTime":5000,"endTime":4000,"loopTime":100}}}""")!;
        Assert.Equal(TimeSpan.FromSeconds(30), t.CloseDelay);
        Assert.Equal(t.CloseDelay, t.InfoDelay);
        Assert.Null(t.VideoEnd);
        Assert.Null(t.LoopFrom);
    }

    [Fact]
    public void ConfigIsFoundAboveTheVideo_AsHeyMelodyLaysPacksOut()
    {
        string pack = Path.Combine(_root, "assets", "abc");
        string video = Path.Combine(pack, "res", "video-night", "boot.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        File.WriteAllBytes(video, [0]);
        File.WriteAllText(Path.Combine(pack, "config.json"), X4Like);
        Assert.Equal(TimeSpan.FromMilliseconds(7500), PopupTimeline.TryReadFor(video)!.CloseDelay);
    }

    [Fact]
    public void MediaWithoutConfig_HasNoTimeline()
    {
        string video = Path.Combine(_root, "custom", "my.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        File.WriteAllBytes(video, [0]);
        Assert.Null(PopupTimeline.TryReadFor(video));
    }
}

public sealed class FullscreenCoverTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void ExactBorderlessFullscreen_Covers() => Assert.True(FullscreenDetector.Covers(new PixelRect(0, 0, 2560, 1440), Monitor));

    [Fact]
    public void SlightOvershootOrUndershoot_Covers()
    {
        Assert.True(FullscreenDetector.Covers(new PixelRect(-8, -8, 2576, 1456), Monitor));
        Assert.True(FullscreenDetector.Covers(new PixelRect(1, 1, 2558, 1438), Monitor));
    }

    [Fact]
    public void MaximizedWindowAboveTheTaskbar_DoesNotCover() =>
        Assert.False(FullscreenDetector.Covers(new PixelRect(0, 0, 2560, 1392), Monitor));

    [Fact]
    public void SecondMonitorWindow_DoesNotCoverThePrimary() =>
        Assert.False(FullscreenDetector.Covers(new PixelRect(2560, 0, 1920, 1080), Monitor));
}

public sealed class PopupPositionTests
{
    // 1920x1080 monitor, taskbar at the bottom (48 px).
    private static readonly PixelRect Work = new(0, 0, 1920, 1032);

    [Theory]
    [InlineData(EncoDeck.Core.Configuration.PopupPosition.BottomRight, 1920 - 12 - 340, 1032 - 12 - 368)]
    [InlineData(EncoDeck.Core.Configuration.PopupPosition.BottomLeft, 12, 1032 - 12 - 368)]
    [InlineData(EncoDeck.Core.Configuration.PopupPosition.BottomCenter, (1920 - 340) / 2, 1032 - 12 - 368)]
    [InlineData(EncoDeck.Core.Configuration.PopupPosition.TopLeft, 12, 12)]
    [InlineData(EncoDeck.Core.Configuration.PopupPosition.TopCenter, (1920 - 340) / 2, 12)]
    [InlineData(EncoDeck.Core.Configuration.PopupPosition.TopRight, 1920 - 12 - 340, 12)]
    public void CornersAndEdges(EncoDeck.Core.Configuration.PopupPosition position, int x, int y)
    {
        var rect = PopupPlacement.PlaceAt(position, Work, TaskbarEdge.Bottom, 340, 368, 12);
        Assert.Equal(new PixelRect(x, y, 340, 368), rect);
        Assert.True(Work.Contains(rect));
    }

    [Fact]
    public void NearTray_FollowsTheTaskbar()
    {
        var bottom = PopupPlacement.PlaceAt(EncoDeck.Core.Configuration.PopupPosition.NearTray, Work, TaskbarEdge.Bottom, 340, 368, 12);
        Assert.Equal(PopupPlacement.PlaceNearTaskbar(Work, TaskbarEdge.Bottom, null, 340, 368, 12), bottom);
        Assert.True(PopupPlacement.IsTop(EncoDeck.Core.Configuration.PopupPosition.NearTray, TaskbarEdge.Top));
        Assert.False(PopupPlacement.IsTop(EncoDeck.Core.Configuration.PopupPosition.NearTray, TaskbarEdge.Bottom));
    }

    [Fact]
    public void OversizedPopup_StaysInsideTheWorkArea()
    {
        var small = new PixelRect(100, 50, 400, 300);
        var rect = PopupPlacement.PlaceAt(EncoDeck.Core.Configuration.PopupPosition.TopRight, small, TaskbarEdge.Bottom, 900, 900, 12);
        Assert.True(small.Contains(rect));
    }

    [Fact]
    public void Settings_ClampTheScale()
    {
        Assert.Equal(1.6, new EncoDeck.Core.Configuration.AppSettings { PopupScale = 9 }.Normalize().PopupScale);
        Assert.Equal(0.6, new EncoDeck.Core.Configuration.AppSettings { PopupScale = 0.1 }.Normalize().PopupScale);
        Assert.Equal(1.0, new EncoDeck.Core.Configuration.AppSettings { PopupScale = double.NaN }.Normalize().PopupScale);
    }
}

public sealed class AnimatedWebpTests
{
    /// <summary>A synthetic two-frame animated WebP (frame bodies are placeholder chunks, never decoded).</summary>
    private static byte[] Sample()
    {
        static byte[] Chunk(string tag, byte[] body)
        {
            var c = new List<byte>(System.Text.Encoding.ASCII.GetBytes(tag));
            c.AddRange(BitConverter.GetBytes(body.Length));
            c.AddRange(body);
            if (body.Length % 2 == 1) c.Add(0);
            return [.. c];
        }
        static byte[] U24(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];
        static byte[] Anmf(int x, int y, int w, int h, byte flags, byte[] inner) =>
            Chunk("ANMF", [.. U24(x / 2), .. U24(y / 2), .. U24(w - 1), .. U24(h - 1), .. U24(33), flags, .. inner]);

        byte[] frameA = [.. Chunk("ALPH", [1, 2, 3]), .. Chunk("VP8 ", [9, 9, 9, 9])];
        byte[] frameB = [.. Chunk("VP8 ", [7, 7])];
        var chunks = new List<byte>();
        chunks.AddRange(Chunk("VP8X", [0x12, 0, 0, 0, .. U24(779), .. U24(779)]));
        chunks.AddRange(Chunk("ANIM", [255, 255, 255, 255, 0, 0]));
        chunks.AddRange(Anmf(114, 152, 552, 484, 0x02, frameA));
        chunks.AddRange(Anmf(112, 152, 554, 484, 0x03, frameB));
        var file = new List<byte>(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        file.AddRange(BitConverter.GetBytes(4 + chunks.Count));
        file.AddRange(System.Text.Encoding.ASCII.GetBytes("WEBP"));
        file.AddRange(chunks);
        return [.. file];
    }

    [Fact]
    public void Parse_ReadsCanvasAndFrames()
    {
        var webp = EncoDeck.Platform.Media.AnimatedWebp.TryParse(Sample())!;
        Assert.Equal((780, 780), (webp.CanvasWidth, webp.CanvasHeight));
        Assert.Equal(2, webp.Frames.Count);
        var f = webp.Frames[0];
        Assert.Equal((114, 152, 552, 484, 33), (f.X, f.Y, f.Width, f.Height, f.DurationMs));
        Assert.False(f.Blend);
        Assert.False(f.DisposeToBackground);
        Assert.True(webp.Frames[1].DisposeToBackground);
        Assert.True(webp.FramesAreIndependent);
    }

    [Fact]
    public void StillFrame_IsAValidStandaloneWebp()
    {
        var webp = EncoDeck.Platform.Media.AnimatedWebp.TryParse(Sample())!;
        byte[] still = webp.ToStillWebp(0);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(still, 0, 4));
        Assert.Equal(still.Length - 8, BitConverter.ToInt32(still, 4));
        Assert.Equal("WEBPVP8X", System.Text.Encoding.ASCII.GetString(still, 8, 8));
        Assert.Equal(0x10, still[20]); // alpha, because the frame has an ALPH chunk
        Assert.Equal(551, still[24] | (still[25] << 8) | (still[26] << 16));
        Assert.Equal(483, still[27] | (still[28] << 8) | (still[29] << 16));
        Assert.Equal("ALPH", System.Text.Encoding.ASCII.GetString(still, 30, 4));
        Assert.Equal(0, webp.ToStillWebp(1)[20]); // no ALPH chunk: no alpha flag
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' })]
    public void NotAnAnimation_IsNull(byte[] data) => Assert.Null(EncoDeck.Platform.Media.AnimatedWebp.TryParse(data));
}

public sealed class PopupSettingsUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EncoDeckTests-set-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SettingsFromAnOlderVersion_GetTheStandardPopupSize()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "settings.json");
        // Saved before the popup position/size settings existed.
        File.WriteAllText(path, """{"schemaVersion":1,"showConnectionPopup":true,"popupDurationSeconds":4,"suppressPopupWhenFullscreen":true}""");
        var store = new EncoDeck.Platform.Settings.JsonSettingsStore(path, Microsoft.Extensions.Logging.Abstractions.NullLogger<EncoDeck.Platform.Settings.JsonSettingsStore>.Instance);
        Assert.Equal(1.0, store.Current.PopupScale);
        Assert.Equal(EncoDeck.Core.Configuration.PopupPosition.NearTray, store.Current.PopupPosition);
    }
}
