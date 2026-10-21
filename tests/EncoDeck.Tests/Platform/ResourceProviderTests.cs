// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using EncoDeck.Core.Resources;
using EncoDeck.Platform.Resources;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Platform;

public sealed class ResourceProviderTests : IDisposable
{
    private static readonly DeviceResourceKey X4 = new("oppo-enco-x4", 0x06EC10, 2);

    private readonly string _root = Directory.CreateTempSubdirectory("encodeck-res").FullName;
    private string UserRoot => Path.Combine(_root, "user");
    private string BundledRoot => Path.Combine(_root, "bundled");

    private FileDeviceResourceProvider Create() => new(UserRoot, BundledRoot, NullLogger<FileDeviceResourceProvider>.Instance);

    private static void WritePack(string folder, string manifestName, ResourcePackManifest manifest, params string[] files)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, manifestName), JsonSerializer.Serialize(manifest, ResourcePackJsonContext.Default.ResourcePackManifest));
        foreach (var file in files)
        {
            string path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
        }
    }

    private static ResourcePackManifest Manifest(string name, int? colorId, params (string Role, string File)[] resources) => new()
    {
        Name = name,
        Models = [new ResourcePackModel { ProductId = "06EC10", ModelId = "oppo-enco-x4", ColorId = colorId, Resources = resources.ToDictionary(r => r.Role, r => r.File, StringComparer.OrdinalIgnoreCase) }],
    };

    [Fact]
    public void NothingInstalled_ResolvesNull()
    {
        using var provider = Create();
        Assert.Null(provider.Resolve(X4, DeviceResourceRole.Device));
    }

    [Fact]
    public async Task PriorityOrder_CustomThenOnlineThenImportedThenBundled()
    {
        WritePack(Path.Combine(BundledRoot, "b"), "pack.json", Manifest("bundled", null, ("device", "d.png")), "d.png");
        using var provider = Create();
        Assert.Equal(DeviceResourceOrigin.BundledPack, provider.Resolve(X4, DeviceResourceRole.Device)!.Origin);

        WritePack(Path.Combine(UserRoot, "packs", "p"), "pack.json", Manifest("imported", null, ("device", "d.png")), "d.png");
        provider.Invalidate();
        Assert.Equal(DeviceResourceOrigin.ImportedPack, provider.Resolve(X4, DeviceResourceRole.Device)!.Origin);

        WritePack(Path.Combine(UserRoot, "online", "06EC10"), "manifest.json", Manifest("online", null, ("device", "d.png")), "d.png");
        provider.Invalidate();
        Assert.Equal(DeviceResourceOrigin.Official, provider.Resolve(X4, DeviceResourceRole.Device)!.Origin);

        string custom = Path.Combine(_root, "mine.gif");
        File.WriteAllBytes(custom, [9]);
        await provider.SetCustomAsync(X4, DeviceResourceRole.Device, custom, TestContext.Current.CancellationToken);
        var resolved = provider.Resolve(X4, DeviceResourceRole.Device)!;
        Assert.Equal(DeviceResourceOrigin.Custom, resolved.Origin);
        Assert.Equal(DeviceResourceKind.AnimatedImage, resolved.Kind);

        provider.ClearCustom(X4, DeviceResourceRole.Device);
        Assert.Equal(DeviceResourceOrigin.Official, provider.Resolve(X4, DeviceResourceRole.Device)!.Origin);
    }

    [Fact]
    public void ColourSpecificEntry_IsPreferred_ThenColourless()
    {
        var manifest = new ResourcePackManifest
        {
            Name = "colours",
            Models =
            [
                new ResourcePackModel { ProductId = "06EC10", ColorId = null, Resources = new(StringComparer.OrdinalIgnoreCase) { ["device"] = "default.png" } },
                new ResourcePackModel { ProductId = "06EC10", ColorId = 2, Resources = new(StringComparer.OrdinalIgnoreCase) { ["device"] = "white.png" } },
            ],
        };
        WritePack(Path.Combine(UserRoot, "packs", "c"), "pack.json", manifest, "default.png", "white.png");
        using var provider = Create();
        Assert.EndsWith("white.png", provider.Resolve(X4, DeviceResourceRole.Device)!.Path);
        Assert.EndsWith("default.png", provider.Resolve(X4 with { ColorId = 1 }, DeviceResourceRole.Device)!.Path);
        // Entries keyed only by product id cannot match before the product id is known.
        Assert.Null(provider.Resolve(new DeviceResourceKey("oppo-enco-x4", null, null), DeviceResourceRole.Device));
    }

    [Fact]
    public void ExactColour_InAnotherOnlineManifest_BeatsNewerWrongColour()
    {
        string pink = Path.Combine(UserRoot, "online", "06EC10", "2");
        string white = Path.Combine(UserRoot, "online", "06EC10", "1");
        WritePack(pink, "manifest.json", Manifest("pink", 2, ("device", "pink.png")), "pink.png");
        WritePack(white, "manifest.json", Manifest("white", 1, ("device", "white.png")), "white.png");
        File.SetLastWriteTimeUtc(Path.Combine(pink, "manifest.json"), DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(Path.Combine(white, "manifest.json"), DateTime.UtcNow);
        using var provider = Create();
        Assert.EndsWith("pink.png", provider.Resolve(X4, DeviceResourceRole.Device)!.Path);
        Assert.EndsWith("white.png", provider.Resolve(X4 with { ColorId = 1 }, DeviceResourceRole.Device)!.Path);
    }

    [Fact]
    public void SourcePriority_BeatsColourMatchInLowerPrioritySource()
    {
        WritePack(Path.Combine(UserRoot, "online", "default"), "manifest.json",
            Manifest("online", null, ("device", "online.png")), "online.png");
        WritePack(Path.Combine(UserRoot, "packs", "exact"), "pack.json",
            Manifest("imported", 2, ("device", "imported.png")), "imported.png");
        using var provider = Create();
        Assert.EndsWith("online.png", provider.Resolve(X4, DeviceResourceRole.Device)!.Path);
    }

    [Fact]
    public void ModelIdEntry_MatchesBeforeProductIdIsKnown()
    {
        WritePack(Path.Combine(UserRoot, "packs", "m"), "pack.json", Manifest("model", null, ("device", "d.png")), "d.png");
        using var provider = Create();
        Assert.NotNull(provider.Resolve(new DeviceResourceKey("oppo-enco-x4", null, null), DeviceResourceRole.Device));
    }

    [Fact]
    public void VideoPopup_IsClassifiedAsVideo_AndMissingRoleIsNull()
    {
        WritePack(Path.Combine(UserRoot, "packs", "v"), "pack.json", Manifest("video", null, ("popupAnimation", "anim/popup.mp4")), "anim/popup.mp4");
        using var provider = Create();
        Assert.Equal(DeviceResourceKind.Video, provider.Resolve(X4, DeviceResourceRole.PopupAnimation)!.Kind);
        Assert.Null(provider.Resolve(X4, DeviceResourceRole.Case));
    }

    [Fact]
    public void ManifestPathsCannotEscapeThePack()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), [1]);
        WritePack(Path.Combine(UserRoot, "packs", "evil"), "pack.json", Manifest("evil", null, ("device", "..\\..\\..\\outside.png")));
        using var provider = Create();
        Assert.Null(provider.Resolve(X4, DeviceResourceRole.Device));
    }

    [Fact]
    public async Task UnsupportedCustomFile_IsRejected()
    {
        using var provider = Create();
        string exe = Path.Combine(_root, "bad.exe");
        File.WriteAllBytes(exe, [0]);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SetCustomAsync(X4, DeviceResourceRole.Device, exe, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
