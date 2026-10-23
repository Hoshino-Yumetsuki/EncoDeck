// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EncoDeck.Core.Resources;
using EncoDeck.Platform.Resources;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Platform;

public sealed class EncoApiResourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("encoapi-test").FullName;
    private static readonly DeviceResourceKey Key = new("oppo-enco-x4", 0x06EC10, 2);
    private static readonly Uri Api = new("https://encoapi.test/");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pack(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static string Metadata(Dictionary<string, byte[]> packs, string product = "06EC10", int color = 2) => JsonSerializer.Serialize(new
    {
        productId = product, colorId = color, available = true,
        packages = packs.ToDictionary(pair => pair.Key, pair => new { url = new Uri(Api, pair.Key + ".zip").AbsoluteUri, sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) }),
    });

    private static Dictionary<string, byte[]> Fixtures() => new()
    {
        ["control"] = Pack(("config.json", """{"detailImageRes":{"path":"res/image/device.png"},"boxImageRes":{"path":"res/image/case.png"}}"""),
            ("res/image/device.png", "device"), ("res/image/case.png", "case")),
        ["popup"] = Pack(("config.json", """{"videoRes":{"path":"res/video{NIGHT}/boot.mp4"},"discoveryStates":{"backConnectedState":{"closeDelay":7500,"infoFadeInDelay":2500}}}"""), ("res/video/boot.mp4", "video")),
        ["popupDark"] = Pack(("config.json", """{"videoRes":{"path":"res/video{NIGHT}/boot.mp4"}}"""), ("res/video-night/boot.mp4", "night")),
        ["model"] = Pack(("config.json", """{"modelWebp":{"path":"res/raw/model.webp"}}"""), ("res/raw/model.webp", "model")),
    };

    private Handler Respond(Dictionary<string, byte[]> packs, string? metadata = null, bool corrupt = false) => new(request =>
    {
        if (request.RequestUri!.AbsolutePath.Contains("resources")) return new(HttpStatusCode.OK) { Content = new StringContent(metadata ?? Metadata(packs), Encoding.UTF8, "application/json") };
        string kind = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt ? [0, 1, 2] : packs[kind]) };
    });

    [Fact]
    public async Task DownloadsAreVerifiedPublishedAndResolved_WithPopupTimingAndModel()
    {
        using var provider = new FileDeviceResourceProvider(_root, Path.Combine(_root, "bundled"), NullLogger<FileDeviceResourceProvider>.Instance);
        var handler = Respond(Fixtures());
        int invalidated = 0;
        using var client = new EncoApiResourceClient(new HttpClient(handler), provider.DownloadRoot, () => { invalidated++; provider.Invalidate(); }, Api);
        var result = await client.EnsureAsync(Key, cancellationToken: Ct);
        Assert.Equal(5, result.Files);
        Assert.False(result.FromCache);
        Assert.Equal(1, invalidated);
        var image = provider.Resolve(Key, DeviceResourceRole.Device)!;
        Assert.Equal(DeviceResourceOrigin.Downloaded, image.Origin);
        Assert.Equal("device", await File.ReadAllTextAsync(image.Path, Ct));
        var popup = provider.Resolve(Key, DeviceResourceRole.PopupAnimation)!;
        Assert.Equal(DeviceResourceKind.Video, popup.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(7500), PopupTimeline.TryReadFor(popup.Path)!.CloseDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), PopupTimeline.TryReadFor(popup.Path)!.InfoDelay);
        Assert.EndsWith("video-night\\boot.mp4", provider.Resolve(Key, DeviceResourceRole.PopupAnimationDark)!.Path);
        Assert.NotNull(provider.Resolve(Key, DeviceResourceRole.DeviceModel));
        Assert.All(handler.Requests, uri => Assert.DoesNotContain("mac", uri.Query, StringComparison.OrdinalIgnoreCase));
        Assert.Null(provider.Resolve(Key with { ColorId = 1 }, DeviceResourceRole.Device));
    }

    [Fact]
    public async Task FreshLocalCacheDoesNotRequestNetwork_AndForcedUpdateReusesVerifiedArchives()
    {
        var handler = Respond(Fixtures());
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        await client.EnsureAsync(Key, cancellationToken: Ct);
        int count = handler.Requests.Count;
        Assert.True((await client.EnsureAsync(Key, cancellationToken: Ct)).FromCache);
        Assert.Equal(count, handler.Requests.Count);
        await client.EnsureAsync(Key, force: true, Ct);
        Assert.Equal(count + 1, handler.Requests.Count);
        Assert.Contains("fresh=1", handler.Requests[^1].Query);
    }

    private sealed class Recorder : IProgress<ResourceDownloadProgress>
    {
        public List<ResourceDownloadProgress> Reports { get; } = [];
        public void Report(ResourceDownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task ProgressReportsMetadataEachPackageBytesAndExtraction_AndCacheInfo()
    {
        var packs = Fixtures();
        using var client = new EncoApiResourceClient(new HttpClient(Respond(packs)), _root, () => { }, Api);
        Assert.Null(client.GetCacheInfo(Key));
        var progress = new Recorder();
        await client.EnsureAsync(Key, cancellationToken: Ct, progress: progress);

        Assert.Equal(ResourceSyncStage.Checking, progress.Reports[0].Stage);
        for (int package = 1; package <= 4; package++)
        {
            var steps = progress.Reports.Where(r => r.Package == package).ToList();
            Assert.All(steps, r => Assert.Equal(4, r.Packages));
            Assert.Equal(ResourceSyncStage.Downloading, steps[0].Stage);
            // ByteArrayContent sends a Content-Length, so the total is known and the last byte report reaches it.
            var bytes = steps.Last(r => r.Stage == ResourceSyncStage.Downloading);
            Assert.NotNull(bytes.Total);
            Assert.Equal(bytes.Total, bytes.Received);
            Assert.Equal(ResourceSyncStage.Extracting, steps[^1].Stage);
        }

        var info = client.GetCacheInfo(Key)!;
        Assert.Equal(5, info.Files);
        Assert.True(info.Bytes > packs.Values.Sum(p => p.Length), "cache holds the archives and the extracted files");
        Assert.NotNull(info.UpdatedAt);

        // A fresh cache answers without network or progress.
        var again = new Recorder();
        Assert.True((await client.EnsureAsync(Key, cancellationToken: Ct, progress: again)).FromCache);
        Assert.Empty(again.Reports);
    }

    [Fact]
    public async Task UnknownTotalIsReportedAsNull()
    {
        var packs = new Dictionary<string, byte[]> { ["control"] = Fixtures()["control"] };
        var handler = new Handler(request => new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.Contains("resources") ? new StringContent(Metadata(packs))
                : new StreamContent(new NonSeekable(packs["control"])),
        });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        var progress = new Recorder();
        await client.EnsureAsync(Key, cancellationToken: Ct, progress: progress);
        var bytes = progress.Reports.Where(r => r.Stage == ResourceSyncStage.Downloading && r.Received > 0).ToList();
        Assert.NotEmpty(bytes);
        Assert.All(bytes, r => Assert.Null(r.Total));
        Assert.Equal(packs["control"].Length, bytes[^1].Received);
    }

    [Fact]
    public async Task UnavailableProductIsDistinguishedFromBrokenData()
    {
        string metadata = JsonSerializer.Serialize(new { productId = "06EC10", colorId = 2, available = false, packages = new { } });
        using var client = new EncoApiResourceClient(new HttpClient(Respond([], metadata)), _root, () => { }, Api);
        var error = await Assert.ThrowsAsync<ResourceUnavailableException>(() => client.EnsureAsync(Key, cancellationToken: Ct));
        Assert.Equal(ResourceSyncFailure.NotAvailable, DeviceResourceStatus.Classify(error));
    }

    /// <summary>A stream without a length, so the response has no Content-Length.</summary>
    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    [Fact]
    public async Task MissingExtractedFileIsRepairedFromTheVerifiedLocalZip()
    {
        var packs = Fixtures();
        var handler = Respond(packs);
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        await client.EnsureAsync(Key, cancellationToken: Ct);
        int count = handler.Requests.Count;
        string missing = Directory.EnumerateFiles(_root, "device.png", SearchOption.AllDirectories).Single();
        File.Delete(missing);
        Assert.False((await client.EnsureAsync(Key, cancellationToken: Ct)).FromCache);
        Assert.Equal(count + 1, handler.Requests.Count); // Metadata only; reconstruct from source.zip.
        Assert.Equal("device", await File.ReadAllTextAsync(Directory.EnumerateFiles(_root, "device.png", SearchOption.AllDirectories).Single(), Ct));
        await client.EnsureAsync(Key, force: true, Ct);
        Assert.Equal(count + 2, handler.Requests.Count);
        Assert.Single(Directory.EnumerateFiles(_root, "device.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UsesTheApiHashCacheInsteadOfTheSupplierWhenPublished()
    {
        byte[] pack = Fixtures()["control"];
        string hash = Convert.ToHexStringLower(SHA256.HashData(pack));
        var metadata = JsonSerializer.Serialize(new { productId = "06EC10", colorId = 2, available = true,
            packages = new { control = new { url = "https://unreachable.heytapimage.com/unused.zip", sha256 = hash, cacheUrl = $"/v1/packages/{hash}.zip" } } });
        var handler = new Handler(request => new(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.Contains("resources")
            ? new StringContent(metadata) : new ByteArrayContent(pack) });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        Assert.Equal(2, (await client.EnsureAsync(Key, cancellationToken: Ct)).Files);
        Assert.Equal($"/v1/packages/{hash}.zip", handler.Requests[1].AbsolutePath);
        Assert.All(handler.Requests, uri => Assert.Equal(Api.Host, uri.Host));
    }

    [Fact]
    public async Task CacheServiceFailureFallsBackToThePublishedSupplierWithTheSameChecksum()
    {
        byte[] pack = Fixtures()["control"];
        string hash = Convert.ToHexStringLower(SHA256.HashData(pack));
        var metadata = JsonSerializer.Serialize(new { productId = "06EC10", colorId = 2, available = true,
            packages = new { control = new { url = "https://cdn.heytapimage.com/control.zip", sha256 = hash, cacheUrl = $"/v1/packages/{hash}.zip" } } });
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.Contains("resources")
            ? new(HttpStatusCode.OK) { Content = new StringContent(metadata) }
            : request.RequestUri.Host == Api.Host ? new(HttpStatusCode.BadGateway)
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(pack) });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        Assert.Equal(2, (await client.EnsureAsync(Key, cancellationToken: Ct)).Files);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("cdn.heytapimage.com", handler.Requests[^1].Host);
    }

    [Fact]
    public async Task HashFailurePreservesThePreviousManifestAndImages()
    {
        var packs = Fixtures();
        using (var client = new EncoApiResourceClient(new HttpClient(Respond(packs)), _root, () => { }, Api))
            await client.EnsureAsync(Key, cancellationToken: Ct);
        string manifest = Path.Combine(_root, "06EC10", "2", "manifest.json");
        byte[] previous = await File.ReadAllBytesAsync(manifest, Ct);
        packs["control"] = Pack(("config.json", "{}"), ("image.png", "changed"));
        using var broken = new EncoApiResourceClient(new HttpClient(Respond(packs, corrupt: true)), _root, () => { }, Api);
        await Assert.ThrowsAsync<InvalidDataException>(() => broken.EnsureAsync(Key, force: true, Ct));
        Assert.Equal(previous, await File.ReadAllBytesAsync(manifest, Ct));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("/absolute.png")]
    [InlineData("C:/outside.png")]
    [InlineData("res\\outside.png")]
    public async Task UnsafeArchiveCannotPublishOrWriteOutsideTheCache(string name)
    {
        var packs = new Dictionary<string, byte[]> { ["control"] = Pack(("config.json", "{}"), (name, "bad")) };
        using var client = new EncoApiResourceClient(new HttpClient(Respond(packs)), _root, () => { }, Api);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.EnsureAsync(Key, cancellationToken: Ct));
        Assert.Empty(Directory.EnumerateFiles(_root, "manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".download-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MismatchedProductOrColorIsRejected()
    {
        var packs = Fixtures();
        using var client = new EncoApiResourceClient(new HttpClient(Respond(packs, Metadata(packs, color: 1))), _root, () => { }, Api);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.EnsureAsync(Key, cancellationToken: Ct));
        Assert.Empty(Directory.EnumerateFiles(_root, "manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingOptionalPackagesAndLegacyStaticPopupAreSupported()
    {
        var packs = new Dictionary<string, byte[]> { ["popup"] = Pack(("config.json", """{"discoveryStates":{"connectedState":{"budsImageRes":{"path":"buds.png"}}}}"""), ("buds.png", "buds")) };
        using var client = new EncoApiResourceClient(new HttpClient(Respond(packs)), _root, () => { }, Api);
        Assert.Equal(1, (await client.EnsureAsync(Key, cancellationToken: Ct)).Files);
    }

    [Fact]
    public async Task InvalidDownloadHostIsRejectedBeforeContactingIt()
    {
        var packs = Fixtures();
        string metadata = Metadata(packs).Replace(Api.AbsoluteUri + "control.zip", "https://evil.example/control.zip");
        var handler = Respond(packs, metadata);
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.EnsureAsync(Key, cancellationToken: Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransientServerFailureIsRetriedWithoutUserInteraction()
    {
        var packs = Fixtures();
        int calls = 0;
        var handler = new Handler(request =>
        {
            if (Interlocked.Increment(ref calls) == 1) return new(HttpStatusCode.BadGateway);
            if (request.RequestUri!.AbsolutePath.Contains("resources")) return new(HttpStatusCode.OK) { Content = new StringContent(Metadata(packs)) };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(packs[Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath)]) };
        });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        Assert.Equal(5, (await client.EnsureAsync(Key, cancellationToken: Ct)).Files);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task CancelledRequestDoesNotPublish()
    {
        var handler = Respond(Fixtures());
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnsureAsync(Key, cancellationToken: cancelled.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MultiBrandResolvesPublicCatalogNameAndKeepsFamilyCachesSeparate()
    {
        var key = new DeviceResourceKey("redmi-earbuds", null, null, "Redmi Buds 6", "Redmi earbuds");
        var other = key with { DeviceName = "Redmi Buds 5" };
        Assert.NotEqual(key.CacheIdentity, other.CacheIdentity);
        var packs = new Dictionary<string, byte[]>
        {
            ["control"] = Pack(("config.json", """{"detailImageRes":{"path":"device.png"},"lottieRes":{"path":"open.json"}}"""),
                ("device.png", "image"), ("open.json", """{"v":"5.7","layers":[],"fr":30,"ip":0,"op":30}""")),
        };
        var handler = new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/resolve") return new(HttpStatusCode.OK) { Content = new StringContent("""{"productId":"xiaomi-2717-509f","colorId":2}""") };
            if (path.Contains("resources")) return new(HttpStatusCode.OK) { Content = new StringContent(Metadata(packs, "xiaomi-2717-509f", 2)) };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(packs["control"]) };
        });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        Assert.Equal(2, (await client.EnsureAsync(key, cancellationToken: Ct)).Files);
        Assert.Equal(3, handler.Requests.Count);
        var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(client.GetCacheInfo(key)!.Folder, "manifest.json"), Ct), ResourcePackJsonContext.Default.ResourcePackManifest)!;
        Assert.Equal(key.CacheIdentity, manifest.Models[0].ResourceIdentity);
        Assert.Equal("popupAnimation", manifest.Models[0].Resources.Keys.Single(k => k == "popupAnimation"));
        Assert.True((await client.EnsureAsync(key, cancellationToken: Ct)).FromCache);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(DeviceResourceKind.Lottie, IDeviceResourceProvider.KindOf("open.json"));
    }

    [Fact]
    public async Task MoondropArtworkDownloadsByExactModelWithoutSendingPrivateBluetoothName()
    {
        var key = new DeviceResourceKey("moondrop-edge", null, null, "Private nickname", "MOONDROP EDGE");
        Assert.True(key.CanDownload);
        Assert.False(key.UsesOplusId);
        Assert.Equal("moondrop-edge", OfficialArtworkCatalog.Lookup(key)!.Value.ModelId);
        var packs = new Dictionary<string, byte[]>
        {
            ["control"] = Pack(("config.json", """{"detailImageRes":{"path":"device.png"}}"""), ("device.png", "image")),
        };
        var handler = new Handler(request =>
        {
            Assert.DoesNotContain("Private", request.RequestUri!.OriginalString);
            if (request.RequestUri.AbsolutePath == "/v1/resolve")
            {
                Assert.Contains("modelId=moondrop-edge", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"productId":"moondrop-edge","colorId":0,"colorSelection":"model-specific"}""") };
            }
            if (request.RequestUri.AbsolutePath.Contains("resources"))
                return new(HttpStatusCode.OK) { Content = new StringContent(Metadata(packs, "moondrop-edge", 0)) };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(packs["control"]) };
        });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        Assert.Equal(1, (await client.EnsureAsync(key, cancellationToken: Ct)).Files);
        Assert.True((await client.EnsureAsync(key, cancellationToken: Ct)).FromCache);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task FrameAnimationAndHomeModelHaveSeparateResourceRoles()
    {
        var packs = new Dictionary<string, byte[]>
        {
            ["control"] = Pack(("config.json", """{"detailImageRes":{"path":"device.png"},"animationRes":{"path":"open.webp"}}"""),
                ("device.png", "image"), ("open.webp", "frames")),
            ["model"] = Pack(("config.json", """{"model3D":{"path":"scene.vfxms"}}"""), ("scene.vfxms", "scene")),
        };
        using var client = new EncoApiResourceClient(new HttpClient(Respond(packs)), _root, () => { }, Api);
        Assert.Equal(3, (await client.EnsureAsync(Key, cancellationToken: Ct)).Files);
        var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(client.GetCacheInfo(Key)!.Folder, "manifest.json"), Ct), ResourcePackJsonContext.Default.ResourcePackManifest)!;
        Assert.EndsWith("open.webp", manifest.Models[0].Resources["popupAnimation"]);
        Assert.EndsWith("scene.vfxms", manifest.Models[0].Resources["deviceModel"]);
    }

    [Fact]
    public async Task CustomizedBluetoothNameStaysLocalWhenKnownProfileSuppliesTheModel()
    {
        var key = new DeviceResourceKey("huawei-FreeBudsPro3", null, null, "Private personal nickname", "HUAWEI FreeBuds Pro 3");
        var handler = new Handler(request =>
        {
            Assert.DoesNotContain("Private", request.RequestUri!.OriginalString);
            return new(HttpStatusCode.NotFound);
        });
        using var client = new EncoApiResourceClient(new HttpClient(handler), _root, () => { }, Api);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.EnsureAsync(key, cancellationToken: Ct));
        Assert.Single(handler.Requests);
        Assert.Contains("FreeBuds", handler.Requests[0].OriginalString);
        var unknown = key with { ModelId = "huawei-generic", ModelName = "HUAWEI earbuds" };
        await Assert.ThrowsAsync<ResourceUnavailableException>(() => client.EnsureAsync(unknown, cancellationToken: Ct));
        Assert.Single(handler.Requests);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
