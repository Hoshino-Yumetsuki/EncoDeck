// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Tests.Platform;

public sealed class LhdcDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EncoDeck-Lhdc-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallsVerifiedPackageAndReusesItWithoutNetwork()
    {
        byte[] zip = Package();
        int requests = 0;
        using var http = Client(zip, () => requests++);
        var service = new LhdcDownloadService(http, _root, "1.2.0");
        Assert.Null(service.InstalledDirectory);
        Assert.Equal(0, requests);
        await service.DownloadAsync(ct: TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(service.InstalledDirectory));
        Assert.Equal(2, requests);
        await service.DownloadAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task RejectsCorruptDownloadWithoutActivatingIt()
    {
        using var http = Client(Package(), digest: new string('0', 64));
        var service = new LhdcDownloadService(http, _root, "1.2.0");
        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.DownloadAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(service.InstalledDirectory);
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RejectsIncompletePackageAndCanRetry()
    {
        using var http = Client(Package(incomplete: true));
        var service = new LhdcDownloadService(http, _root, "1.2.0");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(service.InstalledDirectory);
        Assert.Empty(Directory.GetDirectories(_root, ".install-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NewAppVersionCannotReuseOrDownloadAnOlderRelease()
    {
        using var http = Client(Package());
        await new LhdcDownloadService(http, _root, "1.2.0").DownloadAsync(ct: TestContext.Current.CancellationToken);
        var upgraded = new LhdcDownloadService(http, _root, "1.3.0");
        Assert.Null(upgraded.InstalledDirectory);
        Assert.EndsWith("/releases/tags/v1.3.0", upgraded.ReleasesApi.AbsoluteUri);
        await Assert.ThrowsAsync<InvalidDataException>(() => upgraded.DownloadAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(upgraded.InstalledDirectory);
    }

    [Fact]
    public async Task RejectsPackageManifestFromAnotherAppVersion()
    {
        using var http = Client(Package(componentVersion: "1.1.0"));
        var service = new LhdcDownloadService(http, _root, "1.2.0");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(service.InstalledDirectory);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("folder/../../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("file:stream")]
    public void RejectsArchiveTraversal(string entry)
    {
        Directory.CreateDirectory(_root);
        string zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry(entry);
        Assert.Throws<InvalidDataException>(() => LhdcDownloadService.ExtractPackage(zip, Path.Combine(_root, "stage"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "escape.exe")));
    }

    [Fact]
    public async Task CancellationDoesNotActivatePackage()
    {
        using var http = Client(Package());
        var service = new LhdcDownloadService(http, _root, "1.2.0");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(ct: cancellation.Token));
        Assert.Null(service.InstalledDirectory);
    }

    private static HttpClient Client(byte[] zip, Action? requested = null, string? digest = null) => new(new Handler(request =>
    {
        requested?.Invoke();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(JsonSerializer.Serialize(new
            {
                draft = false, prerelease = false, tag_name = "v1.2.0",
                assets = new[] { new { name = "EncoDeck-LHDC-1.2.0-win-x64.zip", size = zip.Length,
                    digest = "sha256:" + (digest ?? Convert.ToHexStringLower(SHA256.HashData(zip))),
                    browser_download_url = "https://github.com/TIANLI0/EncoDeck/releases/download/v1.2.0/EncoDeck-LHDC-1.2.0-win-x64.zip" } },
            })) : new ByteArrayContent(zip),
        };
    }));

    private static byte[] Package(bool incomplete = false, string componentVersion = "1.2.0")
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            string[] files = ["backend/lhdc-host.exe", "backend/lhdc-service.exe", "backend/lhdc-device.exe", "backend/lhdc-audio-device.exe", "checksums.json", "scripts/Set-AudioMode.ps1", "runtime/powershell/pwsh.exe",
                "drivers/lhdc-test.cer", "drivers/audio/lhdc-audio.sys", "drivers/transport/lhdc-transport.sys"];
            foreach (string file in files) { using var writer = new StreamWriter(archive.CreateEntry(file).Open()); writer.Write("fixture"); }
            using (var runtime = new StreamWriter(archive.CreateEntry("runtime.json").Open())) runtime.Write(JsonSerializer.Serialize(new { Schema = 1, EncoDeckVersion = componentVersion, SourceRepository = LhdcDownloadService.Repository, Architecture = "x64" }));
            if (!incomplete)
            {
                using var writer = new StreamWriter(archive.CreateEntry("release.json").Open());
                writer.Write(JsonSerializer.Serialize(new { SourceCommit = new string('a', 40), SourceTreeDirty = false }));
            }
        }
        return memory.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
