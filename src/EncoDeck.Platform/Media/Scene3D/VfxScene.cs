// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text.Json;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>A material parameter override from the scene config (Filament parameter name and value).</summary>
public sealed record VfxMaterialParameter(string Name, float Value, float[] Color, bool IsColor);

/// <summary>Overrides for one glTF material, applied by index like HeyMelody's <c>MaterialGroupConfig</c>.</summary>
public sealed record VfxMaterialConfig(string Name, IReadOnlyList<VfxMaterialParameter> Parameters)
{
    public float? Float(string name) => Parameters.LastOrDefault(p => !p.IsColor && p.Name == name)?.Value;

    public float[]? Color(string name) => Parameters.LastOrDefault(p => p.IsColor && p.Name == name)?.Color;
}

/// <summary>
/// The scene settings stored in a <c>.vfxms</c> file (HeyMelody's <c>ModelSceneConfig</c> / <c>LightConfig</c>),
/// with the same defaults when a key is missing.
/// </summary>
public sealed record VfxSceneConfig
{
    public string Name { get; init; } = "None";
    /// <summary>0 none, 1 FXAA, 2 TAA, 3 MSAA (HeyMelody's detail page always uses MSAA).</summary>
    public int AntiAliasing { get; init; } = 1;
    public bool EnableSkybox { get; init; } = true;
    public bool EnableBloom { get; init; } = true;
    public bool EnableSsao { get; init; } = true;
    public bool EnableShadow { get; init; } = true;
    public float[] BackgroundColor { get; init; } = [1, 1, 1, 1];
    public float ModelScale { get; init; } = 1;
    public IReadOnlyList<VfxMaterialConfig> Materials { get; init; } = [];
    /// <summary>Directional light illuminance in lux (0 = no direct light).</summary>
    public float LightIntensity { get; init; } = 100_000;
    public float[] LightDirection { get; init; } = [0, -1, 0];
    /// <summary>Image-based light intensity in lux.</summary>
    public float IblIntensity { get; init; } = 30_000;
    /// <summary>Euler angles (degrees) the IBL is rotated by.</summary>
    public float[] IblAngle { get; init; } = [0, 0, 0];

    // Filament's Material.Parameter.Type ordinals used by the config's "type" field.
    private const int FloatType = 4;
    private const int Float4Type = 7;

    public static VfxSceneConfig Parse(ReadOnlySpan<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        var config = new VfxSceneConfig();
        if (root.ValueKind != JsonValueKind.Object) return config;
        var materials = new List<VfxMaterialConfig>();
        if (root.TryGetProperty("materials", out var group) && group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("materials", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var material in list.EnumerateArray())
            {
                var parameters = new List<VfxMaterialParameter>();
                if (material.TryGetProperty("property", out var properties) && properties.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in properties.EnumerateArray())
                    {
                        int type = p.TryGetProperty("type", out var t) && t.TryGetInt32(out int ti) ? ti : FloatType;
                        if (type is not (FloatType or Float4Type)) continue;
                        parameters.Add(new VfxMaterialParameter(Text(p, "name") ?? "", Number(p, "value") ?? 1, Floats(p, "color", 4) ?? [0, 0, 0, 0], type == Float4Type));
                    }
                }
                materials.Add(new VfxMaterialConfig(Text(material, "name") ?? "", parameters));
            }
        }
        var light = root.TryGetProperty("light", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        return config with
        {
            Name = Text(root, "name") ?? config.Name,
            AntiAliasing = root.TryGetProperty("aaType", out var aa) && aa.TryGetInt32(out int aaType) ? aaType : config.AntiAliasing,
            EnableSkybox = Bool(root, "enableSkyBox") ?? config.EnableSkybox,
            EnableBloom = Bool(root, "enableBloom") ?? config.EnableBloom,
            EnableSsao = Bool(root, "enableSSAO") ?? config.EnableSsao,
            EnableShadow = Bool(root, "enableShadow") ?? config.EnableShadow,
            BackgroundColor = Floats(root, "backgroundColor", 4) ?? config.BackgroundColor,
            ModelScale = Number(root, "modelScale") ?? config.ModelScale,
            Materials = materials,
            LightIntensity = light.ValueKind == JsonValueKind.Object ? Number(light, "intensity") ?? config.LightIntensity : config.LightIntensity,
            LightDirection = light.ValueKind == JsonValueKind.Object ? Floats(light, "direction", 3) ?? config.LightDirection : config.LightDirection,
            IblIntensity = light.ValueKind == JsonValueKind.Object ? Number(light, "iblIntensity") ?? config.IblIntensity : config.IblIntensity,
            IblAngle = light.ValueKind == JsonValueKind.Object ? Floats(light, "iblAngle", 3) ?? config.IblAngle : config.IblAngle,
        };
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static float? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;

    private static float[]? Floats(JsonElement e, string name, int count)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < count) return null;
        var result = new float[count];
        int i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (i == count) break;
            if (item.ValueKind != JsonValueKind.Number) return null;
            result[i++] = (float)item.GetDouble();
        }
        return result;
    }
}

/// <summary>
/// HeyMelody's 3D detail model (<c>.vfxms</c>, OPPO's Filament "VFX model scene"): a 40-byte
/// big-endian header of (start, length) pairs for config, glb, ibl, skybox and animation, followed
/// by the scene config (JSON), a binary glTF, a Filament IBL cubemap (KTX, with spherical harmonics)
/// and a skybox (KTX). Like <c>ModelScene.loadSceneFromBuffer</c>, the sections are read one after
/// another after the header; the start offsets are only checked.
/// </summary>
public sealed class VfxScene
{
    public const int HeaderSize = 40;

    private VfxScene(VfxSceneConfig config, ReadOnlyMemory<byte> glb, ReadOnlyMemory<byte> ibl, ReadOnlyMemory<byte> skybox)
    {
        Config = config;
        Glb = glb;
        Ibl = ibl;
        Skybox = skybox;
    }

    public VfxSceneConfig Config { get; }
    public ReadOnlyMemory<byte> Glb { get; }
    /// <summary>Prefiltered specular cubemap with SH ("sh" key) for the indirect light; may be empty.</summary>
    public ReadOnlyMemory<byte> Ibl { get; }
    public ReadOnlyMemory<byte> Skybox { get; }

    public static bool IsSceneFile(string path) => path.EndsWith(".vfxms", StringComparison.OrdinalIgnoreCase);

    public static VfxScene Parse(ReadOnlyMemory<byte> file)
    {
        var data = file.Span;
        if (data.Length < HeaderSize) throw new InvalidDataException("The model file is too short.");
        var header = new int[10];
        for (int i = 0; i < header.Length; i++) header[i] = BinaryPrimitives.ReadInt32BigEndian(data[(i * 4)..]);
        int position = HeaderSize;
        ReadOnlyMemory<byte> Next(int index)
        {
            int start = header[index * 2], length = header[index * 2 + 1];
            if (length <= 0) return ReadOnlyMemory<byte>.Empty;
            if (length > file.Length - position) throw new InvalidDataException("A model section exceeds the file.");
            if (start != 0 && start != position) throw new InvalidDataException("Unexpected model section layout.");
            var slice = file.Slice(position, length);
            position += length;
            return slice;
        }
        var config = Next(0);
        var glb = Next(1);
        var ibl = Next(2);
        var skybox = Next(3);
        if (config.IsEmpty || glb.IsEmpty) throw new InvalidDataException("The model file has no scene or no model.");
        return new VfxScene(VfxSceneConfig.Parse(config.Span), glb, ibl, skybox);
    }
}
