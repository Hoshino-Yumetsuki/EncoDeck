// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace EncoDeck.Platform.Components;

/// <summary>One downloadable archive of a component, pinned by size and SHA-256.</summary>
public sealed record ComponentPackage
{
    /// <summary>"x64" or "arm64".</summary>
    public required string Architecture { get; init; }

    public required string File { get; init; }

    public long Size { get; init; }

    /// <summary>Lower-case hex; empty while the component release is not published yet.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Folder inside the archive that holds the component (e.g. "A2DPWB-2.0.1"); empty for the archive root.</summary>
    public string Root { get; init; } = "";

    /// <summary>Other sources of the same bytes, tried after the EncoDeck component release; verified against the same digest.</summary>
    public IReadOnlyList<Uri> Mirrors { get; init; } = [];

    public bool IsPublished => Size > 0 && Sha256.Length == 64;
}

/// <summary>Where a component's code comes from (shown to the user and checked against the package).</summary>
public sealed record ComponentSource(string Repository, string Commit, string Release);

/// <summary>
/// A component release: its own tag in the EncoDeck repository, independent of app releases, so an app
/// update neither rebuilds nor re-downloads a component whose pin did not change.
/// </summary>
public sealed record ComponentRelease
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Tag { get; init; }

    public required ComponentSource Source { get; init; }

    public IReadOnlyList<ComponentPackage> Packages { get; init; } = [];

    /// <summary>The package for this machine: arm64 Windows gets an arm64 build when there is one (the app itself may run emulated).</summary>
    public ComponentPackage? PackageFor(Architecture os) =>
        Packages.FirstOrDefault(p => p.Architecture == (os == Architecture.Arm64 ? "arm64" : "x64"))
        ?? (os == Architecture.Arm64 ? Packages.FirstOrDefault(p => p.Architecture == "x64") : null);

    public ComponentPackage? CurrentPackage => PackageFor(RuntimeInformation.OSArchitecture);

    /// <summary>The EncoDeck component release first, then the package's mirrors.</summary>
    public IReadOnlyList<Uri> Sources(ComponentPackage package) =>
        [new Uri($"https://github.com/{ComponentCatalog.Repository}/releases/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(package.File)}"), .. package.Mirrors];
}

/// <summary>The component pins built into this app (<c>components.json</c>); no network lookup decides what is installed.</summary>
public static class ComponentCatalog
{
    public const string Repository = "TIANLI0/EncoDeck";
    public const string LhdcId = "lhdc";
    public const string A2dpBridgeId = "a2dpwb";

    private static readonly Lazy<IReadOnlyList<ComponentRelease>> All = new(() =>
    {
        using var stream = typeof(ComponentCatalog).Assembly.GetManifestResourceStream("EncoDeck.Platform.Components.components.json")
            ?? throw new InvalidOperationException("components.json is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static IReadOnlyList<ComponentRelease> Components => All.Value;

    public static ComponentRelease Lhdc => Get(LhdcId);

    public static ComponentRelease A2dpBridge => Get(A2dpBridgeId);

    public static ComponentRelease Get(string id) => Components.FirstOrDefault(c => c.Id == id)
        ?? throw new KeyNotFoundException($"No component '{id}'.");

    /// <summary>Reads and validates a manifest; anything malformed is rejected rather than half-used.</summary>
    internal static IReadOnlyList<ComponentRelease> Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty component manifest.");
        if (root["schema"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported component manifest schema.");
        var result = new List<ComponentRelease>();
        foreach (var node in root["components"]?.AsArray() ?? throw new InvalidDataException("No components."))
        {
            var c = node?.AsObject() ?? throw new InvalidDataException("Invalid component.");
            string id = Text(c, "id"), version = Text(c, "version"), tag = Text(c, "tag");
            if (!IsName(id) || !IsName(version) || !IsName(tag)) throw new InvalidDataException($"Invalid component identity: {id}.");
            var source = c["source"]?.AsObject() ?? throw new InvalidDataException($"{id}: no source.");
            string commit = Text(source, "commit");
            if (commit.Length != 40 || !commit.All(Uri.IsHexDigit)) throw new InvalidDataException($"{id}: invalid source commit.");
            var packages = new List<ComponentPackage>();
            foreach (var p in c["packages"]?.AsArray() ?? throw new InvalidDataException($"{id}: no packages."))
            {
                var o = p?.AsObject() ?? throw new InvalidDataException($"{id}: invalid package.");
                string file = Text(o, "file"), sha = (o["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant();
                string root2 = o["root"]?.GetValue<string>() ?? "";
                if (!IsName(file) || Path.GetFileName(file) != file || !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{id}: invalid package file name.");
                if (sha.Length != 0 && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) throw new InvalidDataException($"{id}: invalid SHA-256.");
                if (root2.Length != 0 && (!IsName(root2) || root2.Contains("..", StringComparison.Ordinal))) throw new InvalidDataException($"{id}: invalid package root.");
                var mirrors = (o["mirrors"]?.AsArray() ?? []).Select(m => Uri.TryCreate(m?.GetValue<string>(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                    ? uri : throw new InvalidDataException($"{id}: mirrors must be HTTPS URLs.")).ToArray();
                packages.Add(new ComponentPackage
                {
                    Architecture = Text(o, "architecture") is var arch && arch is "x64" or "arm64" ? arch : throw new InvalidDataException($"{id}: unknown architecture."),
                    File = file,
                    Size = o["size"]?.GetValue<long>() is >= 0 and <= 2L * 1024 * 1024 * 1024 and var size ? size : throw new InvalidDataException($"{id}: invalid size."),
                    Sha256 = sha,
                    Root = root2,
                    Mirrors = mirrors,
                });
            }
            result.Add(new ComponentRelease
            {
                Id = id,
                Version = version,
                Tag = tag,
                Source = new ComponentSource(Text(source, "repository"), commit.ToLowerInvariant(), Text(source, "release")),
                Packages = packages,
            });
        }
        if (result.Select(c => c.Id).Distinct().Count() != result.Count) throw new InvalidDataException("Duplicate component id.");
        return result;
    }

    private static string Text(JsonObject o, string name) =>
        o[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidDataException($"Missing '{name}'.");

    /// <summary>Tags, versions and file names: letters, digits and . _ - only (no separators, no spaces).</summary>
    private static bool IsName(string value) => value.Length is > 0 and <= 128
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') && value[0] != '.';
}
