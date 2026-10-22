// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;
using EncoDeck.Core.Resources;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Resources;

/// <summary>Manifest of a resource pack (<c>pack.json</c>) or of one downloaded official resource set (<c>manifest.json</c>).</summary>
public sealed record ResourcePackManifest
{
    public int Schema { get; init; } = 1;
    public string? Name { get; init; }
    /// <summary>Resource source, e.g. "encoapi" or "custom".</summary>
    public string? Source { get; init; }
    public List<ResourcePackModel> Models { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool RepresentativeArtwork { get; init; }
}

public sealed record ResourcePackModel
{
    /// <summary>6-digit hex product id, e.g. "06EC10".</summary>
    public string? ProductId { get; init; }
    /// <summary>Model id, or <see cref="AnyModel"/> for generic art used when nothing more specific exists.</summary>
    public string? ModelId { get; init; }
    /// <summary>Distinguishes devices sharing a family profile.</summary>
    public string? ResourceIdentity { get; init; }
    public int? ColorId { get; init; }
    /// <summary>Role name (device, case, leftBud, rightBud, popupAnimation) → path relative to the pack folder.</summary>
    public Dictionary<string, string> Resources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public const string AnyModel = "*";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ResourcePackManifest))]
public sealed partial class ResourcePackJsonContext : JsonSerializerContext;

/// <summary>
/// Resolves user-selected files first, followed by downloaded and existing local caches.
/// File changes invalidate the cache automatically.
/// </summary>
public sealed class FileDeviceResourceProvider : IDeviceResourceProvider, IDisposable
{
    private readonly string _userRoot;
    private readonly string _bundledPacksRoot;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<(DeviceResourceKey, DeviceResourceRole), DeviceResourceFile?> _cache = [];
    private List<(string Folder, ResourcePackManifest Manifest, DeviceResourceOrigin Origin)>? _packs;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public FileDeviceResourceProvider(string userResourceRoot, string bundledPacksRoot, ILogger<FileDeviceResourceProvider> logger)
    {
        _userRoot = userResourceRoot;
        _bundledPacksRoot = bundledPacksRoot;
        _logger = logger;
    }

    public string UserRoot => _userRoot;
    public string CustomRoot => Path.Combine(_userRoot, "custom");
    public string OfficialRoot => Path.Combine(_userRoot, "official");
    /// <summary>Legacy cache directory.</summary>
    public string OnlineRoot => Path.Combine(_userRoot, "online");
    public string DownloadRoot => Path.Combine(_userRoot, "downloads");
    public string PacksRoot => Path.Combine(_userRoot, "packs");

    public event EventHandler? Changed;

    public void StartWatching()
    {
        Directory.CreateDirectory(_userRoot);
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(_userRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        _watcher.Changed += OnFileSystemChanged;
        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => Invalidate(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
            _packs = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DeviceResourceFile? Resolve(DeviceResourceKey key, DeviceResourceRole role)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((key, role), out var cached)) return cached;
        }
        var result = ResolveUncached(key, role);
        lock (_gate) _cache[(key, role)] = result;
        return result;
    }

    private DeviceResourceFile? ResolveUncached(DeviceResourceKey key, DeviceResourceRole role)
    {
        // 1. Custom
        string customDir = Path.Combine(CustomRoot, SafeName(key.ModelId));
        if (Directory.Exists(customDir))
        {
            var file = new DirectoryInfo(customDir).EnumerateFiles(RoleName(role) + ".*")
                .Where(f => IDeviceResourceProvider.SupportedExtensions.Contains(f.Extension))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file is not null) return new DeviceResourceFile(file.FullName, IDeviceResourceProvider.KindOf(file.FullName), DeviceResourceOrigin.Custom, null);
        }

        // Downloaded resources and existing local packs. Prefer an exact colour within each source.
        var candidates = Packs()
            .Select(p => (p.Folder, p.Manifest, p.Origin, Model: BestModel(p.Manifest, key)))
            .Where(p => p.Model is not null)
            // Before the earbuds report their colour (not connected yet) any colour of the model will do.
            .Where(p => p.Origin != DeviceResourceOrigin.Downloaded || p.Model!.ColorId is null || key.ColorId is null || p.Model.ColorId == key.ColorId)
            .OrderBy(p => p.Origin)
            .ThenBy(p => p.Model!.ColorId is { } color ? color == key.ColorId ? 0 : 2 : 1);
        foreach (var (folder, manifest, origin, model) in candidates)
        {
            if (model is null || !model.Resources.TryGetValue(RoleName(role), out var relative)) continue;
            string path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!path.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
            return new DeviceResourceFile(path, IDeviceResourceProvider.KindOf(path), origin, manifest.Name, manifest.RepresentativeArtwork);
        }

        // Generic entries in existing local packs are used only as a fallback.
        foreach (var (folder, manifest, origin) in Packs().OrderBy(p => p.Origin))
        {
            var model = manifest.Models.FirstOrDefault(m => m.ModelId == ResourcePackModel.AnyModel);
            if (model is null || !model.Resources.TryGetValue(RoleName(role), out var relative)) continue;
            string path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!path.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
            return new DeviceResourceFile(path, IDeviceResourceProvider.KindOf(path), origin, manifest.Name);
        }
        return null;
    }

    /// <summary>Entry for the device: matched by product id or model id; exact colour first, then colourless, then any.</summary>
    internal static ResourcePackModel? BestModel(ResourcePackManifest manifest, DeviceResourceKey key)
    {
        var matching = manifest.Models.Where(m => m.ResourceIdentity is not null
            ? string.Equals(m.ResourceIdentity, key.CacheIdentity, StringComparison.Ordinal)
            : (
            (m.ProductId is not null && key.ProductId is not null && string.Equals(m.ProductId, key.ProductIdText, StringComparison.OrdinalIgnoreCase)) ||
            (m.ModelId is not null && string.Equals(m.ModelId, key.ModelId, StringComparison.OrdinalIgnoreCase)))).ToList();
        return matching.FirstOrDefault(m => m.ColorId is not null && m.ColorId == key.ColorId)
            ?? matching.FirstOrDefault(m => m.ColorId is null)
            ?? matching.FirstOrDefault();
    }

    private List<(string Folder, ResourcePackManifest Manifest, DeviceResourceOrigin Origin)> Packs()
    {
        lock (_gate)
        {
            if (_packs is not null) return _packs;
        }
        var list = new List<(string, ResourcePackManifest, DeviceResourceOrigin)>();
        AddManifests(list, DownloadRoot, "manifest.json", DeviceResourceOrigin.Downloaded);
        AddManifests(list, OfficialRoot, "manifest.json", DeviceResourceOrigin.Official);
        AddManifests(list, OnlineRoot, "manifest.json", DeviceResourceOrigin.Official);
        AddManifests(list, PacksRoot, "pack.json", DeviceResourceOrigin.ImportedPack);
        AddManifests(list, _bundledPacksRoot, "pack.json", DeviceResourceOrigin.BundledPack);
        lock (_gate) _packs = list;
        return list;
    }

    private void AddManifests(List<(string, ResourcePackManifest, DeviceResourceOrigin)> list, string root, string fileName, DeviceResourceOrigin origin)
    {
        if (!Directory.Exists(root)) return;
        foreach (var manifestFile in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var stream = File.OpenRead(manifestFile);
                var manifest = JsonSerializer.Deserialize(stream, ResourcePackJsonContext.Default.ResourcePackManifest);
                if (manifest is not null) list.Add((Path.GetDirectoryName(manifestFile)!, manifest, origin));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Ignoring unreadable resource manifest {File}", manifestFile);
            }
        }
    }

    public async Task SetCustomAsync(DeviceResourceKey key, DeviceResourceRole role, string sourceFile, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(sourceFile);
        if (!IDeviceResourceProvider.SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException($"Unsupported file type {extension}.");
        }
        string dir = Path.Combine(CustomRoot, SafeName(key.ModelId));
        Directory.CreateDirectory(dir);
        RemoveCustomFiles(dir, role);
        string target = Path.Combine(dir, RoleName(role) + extension.ToLowerInvariant());
        await using (var source = File.OpenRead(sourceFile))
        await using (var destination = File.Create(target))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        Invalidate();
    }

    public void ClearCustom(DeviceResourceKey key, DeviceResourceRole role)
    {
        string dir = Path.Combine(CustomRoot, SafeName(key.ModelId));
        if (Directory.Exists(dir)) RemoveCustomFiles(dir, role);
        Invalidate();
    }

    private static void RemoveCustomFiles(string dir, DeviceResourceRole role)
    {
        foreach (var file in Directory.EnumerateFiles(dir, RoleName(role) + ".*")) File.Delete(file);
    }

    public static string RoleName(DeviceResourceRole role) => role switch
    {
        DeviceResourceRole.Device => "device",
        DeviceResourceRole.Case => "case",
        DeviceResourceRole.LeftBud => "leftBud",
        DeviceResourceRole.RightBud => "rightBud",
        DeviceResourceRole.PopupAnimationDark => "popupAnimationDark",
        DeviceResourceRole.DeviceModel => "deviceModel",
        DeviceResourceRole.DeviceAnimation => "deviceAnimation",
        _ => "popupAnimation",
    };

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
