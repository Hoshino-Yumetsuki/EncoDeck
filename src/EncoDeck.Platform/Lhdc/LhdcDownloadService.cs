// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Text.Json;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Platform.Lhdc;

/// <summary>Installs the official upstream release only when explicitly requested.</summary>
public sealed class LhdcDownloadService(HttpClient http, string cacheRoot, string? appVersion = null)
{
    public const string Repository = "https://github.com/Wayne-u/LHDC-Win";
    public static string ApplicationVersion => typeof(LhdcDownloadService).Assembly.GetName().Version!.ToString(3);
    private readonly SemanticVersion _version = SemanticVersion.Parse(appVersion ?? ApplicationVersion);
    private readonly string root = Path.Combine(cacheRoot, SemanticVersion.Parse(appVersion ?? ApplicationVersion).ToString());
    public Uri ReleasesApi => new($"https://api.github.com/repos/TIANLI0/EncoDeck/releases/tags/v{_version}");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly string[] Required = ["runtime.json", "release.json", "checksums.json",
        "backend/lhdc-host.exe", "backend/lhdc-service.exe", "backend/lhdc-device.exe", "backend/lhdc-audio-device.exe",
        "scripts/Set-AudioMode.ps1", "runtime/powershell/pwsh.exe", "drivers/lhdc-test.cer",
        "drivers/audio/lhdc-audio.sys", "drivers/transport/lhdc-transport.sys"];

    public string? InstalledDirectory
    {
        get
        {
            try
            {
                string digest = File.ReadAllText(Path.Combine(root, "current.txt")).Trim();
                if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) return null;
                string directory = Path.Combine(root, digest);
                if (!Required.All(p => File.Exists(Path.Combine(directory, p)))) return null;
                using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "runtime.json")));
                return runtime.RootElement.GetProperty("EncoDeckVersion").GetString() == _version.ToString() ? directory : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { return null; }
        }
    }

    public async Task DownloadAsync(IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? staging = null;
        try
        {
            if (InstalledDirectory is not null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.UserAgent.ParseAdd("EncoDeck-LHDC/1.0");
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, metadataTimeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(metadataTimeout.Token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, metadataTimeout.Token).ConfigureAwait(false)) != 0)
            {
                if (metadata.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("LHDC metadata is too large.");
                metadata.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(metadata.ToArray());
            var release = json.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("No stable LHDC release is available.");
            string tag = release.GetProperty("tag_name").GetString()!;
            if (tag != $"v{_version}") throw new InvalidDataException("LHDC runtime release does not match this app version.");
            var version = _version;
            string name = $"EncoDeck-LHDC-{version}-win-x64.zip";
            var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("No x64 LHDC package is available.");
            string digestText = asset.GetProperty("digest").GetString() ?? "";
            if (!digestText.StartsWith("sha256:", StringComparison.Ordinal) || digestText.Length != 71 || !digestText[7..].All(Uri.IsHexDigit))
                throw new InvalidDataException("LHDC release has no SHA-256 digest.");
            string digest = digestText[7..].ToLowerInvariant();
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith($"/TIANLI0/EncoDeck/releases/download/v{version}/", StringComparison.Ordinal))
                throw new InvalidDataException("LHDC download is outside the EncoDeck runtime release.");
            var download = new UpdateAsset(name, asset.GetProperty("size").GetInt64(), url, digest);
            var client = new GitHubUpdateClient(http, Path.Combine(root, "downloads"), version);
            string zip = await client.DownloadAsync(new UpdateRelease(version, tag, null, null, null, false, download, null), progress, timeout.Token).ConfigureAwait(false);
            staging = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
            await Task.Run(() => ExtractPackage(zip, staging, timeout.Token, version.ToString()), timeout.Token).ConfigureAwait(false);
            string destination = Path.Combine(root, digest);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
            staging = null;
            string pointer = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(pointer, digest, timeout.Token).ConfigureAwait(false);
            File.Move(pointer, Path.Combine(root, "current.txt"), overwrite: true);
            File.Delete(zip);
        }
        finally
        {
            try
            {
                if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { _gate.Release(); }
        }
    }

    internal static void ExtractPackage(string zip, string destination, CancellationToken ct, string? expectedVersion = null)
    {
        Directory.CreateDirectory(destination);
        string prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("Too many LHDC package entries.");
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            size = checked(size + entry.Length);
            if (size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("LHDC package is too large.");
            string path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe LHDC package path.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        if (!Required.All(p => File.Exists(Path.Combine(destination, p)))) throw new InvalidDataException("Incomplete LHDC package.");
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "runtime.json")));
        if (runtime.RootElement.GetProperty("Schema").GetInt32() != 1
            || runtime.RootElement.GetProperty("SourceRepository").GetString() != Repository
            || runtime.RootElement.GetProperty("Architecture").GetString() != "x64") throw new InvalidDataException("Invalid LHDC runtime identity.");
        if (expectedVersion is not null && runtime.RootElement.GetProperty("EncoDeckVersion").GetString() != expectedVersion)
            throw new InvalidDataException("LHDC component version does not match this app.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "release.json")));
        var source = manifest.RootElement.GetProperty("SourceCommit").GetString();
        if (source is null || source.Length != 40 || !source.All(Uri.IsHexDigit)
            || manifest.RootElement.GetProperty("SourceTreeDirty").GetBoolean()) throw new InvalidDataException("Invalid LHDC source provenance.");
    }
}
