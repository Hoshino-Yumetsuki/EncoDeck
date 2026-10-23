// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EncoDeck.Core.Resources;

namespace EncoDeck.Platform.Resources;

public sealed record ResourceDownloadResult(int Files, string Folder, bool FromCache);

public interface IDeviceResourceDownloader
{
    /// <param name="progress">Called synchronously on the download thread; keep it cheap.</param>
    Task<ResourceDownloadResult> EnsureAsync(DeviceResourceKey key, bool force = false, CancellationToken cancellationToken = default, IProgress<ResourceDownloadProgress>? progress = null);

    /// <summary>What is cached locally for the key (no network, any age); null when nothing is.</summary>
    ResourceCacheInfo? GetCacheInfo(DeviceResourceKey key) => null;
}

internal sealed record ApiResourcePackage(string Url, string Sha256, string? CacheUrl = null);
internal sealed record ApiResourceResponse(string ProductId, int ColorId, bool Available, Dictionary<string, ApiResourcePackage> Packages);
internal sealed record ApiResolvedModel(string ProductId, int ColorId, string? ColorSelection = null);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ApiResourceResponse))]
[JsonSerializable(typeof(ApiResolvedModel))]
internal sealed partial class EncoApiJsonContext : JsonSerializerContext;

/// <summary>Fetches model-specific artwork through EncoAPI and atomically publishes a verified local cache.</summary>
public sealed class EncoApiResourceClient(HttpClient http, string cacheRoot, Action invalidate, Uri? apiBase = null) : IDeviceResourceDownloader, IDisposable
{
    public static Uri DefaultApi { get; } = new("https://encoapi.tianli0.top/");
    private readonly Uri _api = apiBase ?? DefaultApi;
    private const long MaxArchive = 64L * 1024 * 1024;
    private const long MaxExtracted = 384L * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ResourceDownloadResult> EnsureAsync(DeviceResourceKey key, bool force = false, CancellationToken cancellationToken = default, IProgress<ResourceDownloadProgress>? progress = null)
    {
        if (!key.CanDownload)
            throw new InvalidOperationException("A supported model identity is required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string folder = FolderFor(key);
            string manifestFile = Path.Combine(folder, "manifest.json");
            if (!force && ReadCurrent(manifestFile, folder, key) is { } current)
                return new ResourceDownloadResult(current.Models[0].Resources.Count, folder, true);

            progress?.Report(new ResourceDownloadProgress(ResourceSyncStage.Checking));
            string apiId = key.ProductIdText;
            bool representativeArtwork = false;
            int apiColor = key.ColorId ?? 0;
            if (!key.UsesOplusId)
            {
                // Local matching returns public catalog strings, never a customized device name.
                var lookup = OfficialArtworkCatalog.Lookup(key) ?? throw new ResourceUnavailableException("This model is not in the verified artwork catalog.");
                var resolveUrl = new Uri(_api, $"v1/resolve?brand={Uri.EscapeDataString(key.Brand)}&modelId={Uri.EscapeDataString(lookup.ModelId)}"
                    + $"&name={Uri.EscapeDataString(lookup.Name)}"
                    + (key.ColorId is { } knownColor ? $"&color={knownColor}" : "&representative=1"));
                var resolved = JsonSerializer.Deserialize(await GetBytesAsync(resolveUrl, 1024 * 1024, cancellationToken).ConfigureAwait(false),
                    EncoApiJsonContext.Default.ApiResolvedModel) ?? throw new InvalidDataException("Empty model resolution.");
                if (resolved.ProductId is not { Length: >= 1 and <= 80 } || resolved.ProductId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                    || resolved.ColorId is < 0 or > 255 || (key.ColorId is { } reported && reported != resolved.ColorId))
                    throw new InvalidDataException("Invalid model resolution.");
                apiId = resolved.ProductId;
                apiColor = resolved.ColorId;
                representativeArtwork = resolved.ColorSelection is "representative" or "catalog-default";
            }
            var url = new Uri(_api, $"v1/models/{apiId}/resources?color={apiColor}{(force ? "&fresh=1" : string.Empty)}");
            using var metadataStream = new MemoryStream(await GetBytesAsync(url, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
            var metadata = await JsonSerializer.DeserializeAsync(metadataStream, EncoApiJsonContext.Default.ApiResourceResponse, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("EncoAPI returned an empty response.");
            if (!metadata.Available || metadata.ColorId != apiColor || !string.Equals(metadata.ProductId, apiId, StringComparison.OrdinalIgnoreCase)
                || metadata.Packages is null || metadata.Packages.Count == 0)
            {
                if (!metadata.Available || metadata.Packages is not { Count: > 0 }) throw new ResourceUnavailableException("EncoAPI has no resources for this product and color.");
                throw new InvalidDataException("EncoAPI returned resources for a different product or color, or no resources.");
            }

            Directory.CreateDirectory(folder);
            var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] kinds = ["control", "popup", "popupDark", "model"];
            int packages = kinds.Count(metadata.Packages.ContainsKey), index = 0;
            foreach (string kind in kinds)
            {
                if (!metadata.Packages.TryGetValue(kind, out var package)) continue;
                if (package is null) throw new InvalidDataException("Invalid resource package metadata.");
                string pack = await CachePackageAsync(folder, package, progress, ++index, packages, cancellationToken).ConfigureAwait(false);
                using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(pack, "config.json"), cancellationToken).ConfigureAwait(false));
                if (kind == "control")
                {
                    foreach (var (role, field) in new[] { ("device", "detailImageRes"), ("case", "boxImageRes"), ("leftBud", "leftImageRes"), ("rightBud", "rightImageRes") })
                        AddRole(roles, folder, pack, role, Descriptor(config.RootElement, field), false);
                    if (!AddRole(roles, folder, pack, "popupAnimation", Descriptor(config.RootElement, "animationRes"), false))
                        AddRole(roles, folder, pack, "popupAnimation", Descriptor(config.RootElement, "lottieRes"), false);
                    AddRole(roles, folder, pack, "deviceAnimation", Descriptor(config.RootElement, "homeAnimationRes"), false);
                    AddRole(roles, folder, pack, "deviceAnimationDark", Descriptor(config.RootElement, "homeAnimationDarkRes"), false);
                    AddRole(roles, folder, pack, "popupAnimationDark", Descriptor(config.RootElement, "animationDarkRes"), false);
                }
                else if (kind == "model")
                {
                    // HeyMelody's detail page: a pre-rendered turntable (modelWebp) or a Filament scene (model3D, .vfxms).
                    if (!AddRole(roles, folder, pack, "deviceModel", Descriptor(config.RootElement, "modelWebp"), false))
                        AddRole(roles, folder, pack, "deviceModel", Descriptor(config.RootElement, "model3D"), false);
                }
                else
                {
                    bool night = kind == "popupDark";
                    string role = night ? "popupAnimationDark" : "popupAnimation";
                    var root = config.RootElement;
                    var connected = root.TryGetProperty("discoveryStates", out var states) && states.TryGetProperty("connectedState", out var state) ? state : default;
                    foreach (var descriptor in new[] { Descriptor(root, "videoRes"), Descriptor(root, "strangeRes"), Descriptor(connected, "fgAnimRes"), Descriptor(connected, "imageRes"), Descriptor(connected, "budsImageRes") })
                        if (AddRole(roles, folder, pack, role, descriptor, night)) break;
                }
            }
            if (roles.Count == 0) throw new ResourceUnavailableException("No displayable resources were found.");
            var manifest = new ResourcePackManifest
            {
                Name = $"EncoAPI {key.ProductIdText} / {key.ColorId}", Source = "encoapi", UpdatedAt = DateTimeOffset.UtcNow,
                RepresentativeArtwork = representativeArtwork,
                // Product id is sufficient even when the app has no control profile for the model.
                Models = [new ResourcePackModel { ProductId = apiId, ModelId = key.ModelId, ResourceIdentity = key.CacheIdentity, ColorId = key.ColorId, Resources = roles }],
            };
            string temporary = Path.Combine(folder, $".manifest-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = File.Create(temporary))
                    await JsonSerializer.SerializeAsync(output, manifest, ResourcePackJsonContext.Default.ResourcePackManifest, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, manifestFile, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            invalidate();
            return new ResourceDownloadResult(roles.Count, folder, false);
        }
        finally { _gate.Release(); }
    }

    private string FolderFor(DeviceResourceKey key) =>
        Path.GetFullPath(Path.Combine(cacheRoot, key.CacheIdentity, (key.ColorId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public ResourceCacheInfo? GetCacheInfo(DeviceResourceKey key)
    {
        if (!key.CanDownload) return null;
        string folder = FolderFor(key);
        try
        {
            string manifestFile = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestFile)) return null;
            ResourcePackManifest? manifest;
            using (var stream = File.OpenRead(manifestFile))
                manifest = JsonSerializer.Deserialize(stream, ResourcePackJsonContext.Default.ResourcePackManifest);
            if (manifest?.Models is not [var model]) return null;
            int files = model.Resources.Values.Count(relative => SafeRelativePath(relative) && File.Exists(Within(folder, relative)));
            // Everything under the folder: extracted files, the verified source archives and older copies.
            long bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            return new ResourceCacheInfo(files, bytes, manifest.UpdatedAt, folder);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private static ResourcePackManifest? ReadCurrent(string path, string folder, DeviceResourceKey key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize(stream, ResourcePackJsonContext.Default.ResourcePackManifest);
            if (manifest?.Source != "encoapi" || manifest.UpdatedAt is not { } updated || updated > DateTimeOffset.UtcNow
                || DateTimeOffset.UtcNow - updated >= CacheLifetime || manifest.Models.Count != 1) return null;
            var model = manifest.Models[0];
            bool matches = model.ResourceIdentity is not null ? model.ResourceIdentity == key.CacheIdentity
                : key.UsesOplusId && string.Equals(model.ProductId, key.ProductIdText, StringComparison.OrdinalIgnoreCase);
            if (!matches || model.ColorId != key.ColorId || model.Resources.Count == 0) return null;
            return model.Resources.Values.All(relative => SafeRelativePath(relative) && File.Exists(Within(folder, relative))) ? manifest : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private async Task<string> CachePackageAsync(string folder, ApiResourcePackage package, IProgress<ResourceDownloadProgress>? progress, int index, int count, CancellationToken ct)
    {
        progress?.Report(new ResourceDownloadProgress(ResourceSyncStage.Downloading, index, count));
        void Report(long received, long? total) => progress?.Report(new ResourceDownloadProgress(ResourceSyncStage.Downloading, index, count, received, total));
        if (package.Sha256 is not { Length: 64 } || !package.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Missing resource SHA-256.");
        string hash = package.Sha256.ToLowerInvariant();
        string parent = Path.Combine(folder, "assets");
        string target = Path.Combine(parent, hash);
        byte[]? bytes = null;
        // Include previously repaired copies, leaving files used by an open animation untouched.
        foreach (string candidate in Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, hash + "*") : [])
        {
            string existing = Path.Combine(candidate, "source.zip");
            if (!File.Exists(existing) || !File.Exists(Path.Combine(candidate, "config.json"))) continue;
            if (new FileInfo(existing).Length <= MaxArchive)
            {
                var cached = await File.ReadAllBytesAsync(existing, ct).ConfigureAwait(false);
                if (Convert.ToHexStringLower(SHA256.HashData(cached)) == hash)
                {
                    using var archive = new ZipArchive(new MemoryStream(cached), ZipArchiveMode.Read);
                    ValidateArchive(archive);
                    if (archive.Entries.All(entry => entry.FullName.EndsWith('/') ||
                        (File.Exists(Within(candidate, entry.FullName)) && new FileInfo(Within(candidate, entry.FullName)).Length == entry.Length))) return candidate;
                    bytes = cached;
                }
            }
        }
        if (bytes is null)
        {
            Uri uri;
            if (!string.IsNullOrWhiteSpace(package.CacheUrl))
            {
                uri = new Uri(_api, package.CacheUrl);
                if (uri.GetLeftPart(UriPartial.Authority) != _api.GetLeftPart(UriPartial.Authority))
                    throw new InvalidDataException("Unexpected resource cache host.");
            }
            else if (!Uri.TryCreate(package.Url, UriKind.Absolute, out uri!)) throw new InvalidDataException("Invalid resource URL.");
            CheckDownloadUri(uri);
            try { bytes = await GetBytesAsync(uri, MaxArchive, ct, Report).ConfigureAwait(false); }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(package.CacheUrl) && !ct.IsCancellationRequested
                && ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // A cache miss may coincide with a server-region outage. The published supplier
                // URL is an independent route; the same SHA-256 check still applies below.
                if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var source)) throw new InvalidDataException("Invalid resource URL.");
                CheckDownloadUri(source);
                bytes = await GetBytesAsync(source, MaxArchive, ct, Report).ConfigureAwait(false);
            }
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("Resource SHA-256 verification failed.");
        progress?.Report(new ResourceDownloadProgress(ResourceSyncStage.Extracting, index, count));
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            ValidateArchive(archive);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string destination = Within(stage, entry.FullName);
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            if (!File.Exists(Path.Combine(stage, "config.json"))) throw new InvalidDataException("Resource configuration is missing.");
            await File.WriteAllBytesAsync(Path.Combine(stage, "source.zip"), bytes, ct).ConfigureAwait(false);
            // A corrupt older copy is left untouched; publish a new unique folder instead.
            if (Directory.Exists(target)) target += $"-{Guid.NewGuid():N}";
            for (int attempt = 0; ; attempt++)
            {
                // Antivirus or the indexer may still hold the files just written; that clears in moments.
                try { Directory.Move(stage, target); break; }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException && !Directory.Exists(target))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), ct).ConfigureAwait(false);
                }
            }
            return target;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    internal static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 4096) throw new InvalidDataException("Too many resource files.");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            total = checked(total + entry.Length);
            string name = entry.FullName.TrimEnd('/');
            if (!SafeRelativePath(name) || total > MaxExtracted || entry.Length > MaxArchive || !seen.Add(name)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe resource archive or extraction limit exceeded.");
        }
    }

    /// <param name="onProgress">Bytes received so far and the Content-Length, if any.</param>
    private async Task<byte[]> GetBytesAsync(Uri uri, long limit, CancellationToken ct, Action<long, long?>? onProgress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        for (int attempt = 0; ; attempt++)
        {
            try { return await ReadBytesAsync(uri, limit, timeout.Token, onProgress).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode is null or HttpStatusCode.TooManyRequests || (int?)ex.StatusCode >= 500))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), timeout.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, long limit, CancellationToken ct, Action<long, long?>? onProgress)
    {
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            CheckDownloadUri(uri);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                uri = response.Headers.Location is { } location ? new Uri(uri, location) : throw new InvalidDataException("Invalid resource redirect.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Resource download exceeds size limit.");
            long? total = response.Content.Headers.ContentLength;
            onProgress?.Invoke(0, total);
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("Resource download exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onProgress?.Invoke(output.Length, total);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("Too many resource redirects.");
    }

    private void CheckDownloadUri(Uri uri)
    {
        bool api = uri.Scheme == _api.Scheme && uri.Host == _api.Host && uri.Port == _api.Port;
        if (!string.IsNullOrEmpty(uri.UserInfo) || (!api && (uri.Scheme != "https" || !uri.IsDefaultPort
            || !new[] { "heytapimage.com", "heytapmobi.com", "allawntech.com" }.Any(domain => uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidDataException("Unexpected resource download host.");
    }

    private static string? Descriptor(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var resource) && resource.ValueKind == JsonValueKind.Object
        && resource.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;

    private static bool AddRole(Dictionary<string, string> roles, string folder, string pack, string role, string? descriptor, bool night)
    {
        if (string.IsNullOrWhiteSpace(descriptor)) return false;
        foreach (string relative in new[] { descriptor.Replace("{NIGHT}", night ? "-night" : ""), descriptor.Replace("{NIGHT}", "") }.Distinct())
        {
            string file = Within(pack, relative);
            if (File.Exists(file) && IDeviceResourceProvider.SupportedExtensions.Contains(Path.GetExtension(file)))
            {
                roles[role] = Path.GetRelativePath(folder, file).Replace('\\', '/');
                return true;
            }
        }
        return false;
    }

    private static bool SafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length < 1024
        && !Path.IsPathRooted(path) && !path.Any(c => c is '\\' or ':' || char.IsControl(c))
        && !path.Split('/').Any(part => part is ".." or ".");

    private static string Within(string root, string relative)
    {
        if (!SafeRelativePath(relative.TrimEnd('/'))) throw new InvalidDataException("Unsafe resource path.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource path escapes its cache folder.");
        return path;
    }

    public void Dispose() { http.Dispose(); _gate.Dispose(); }
}
