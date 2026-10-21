// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>One vertex as the renderer consumes it (already in the scene's root space).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SceneVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    /// <summary>xyz tangent, w handedness (glTF / MikkTSpace convention).</summary>
    public Vector4 Tangent;
    public Vector2 Uv;

    public const int Size = 48;
}

public sealed record GltfTextureRef(int Image, float Scale);

/// <summary>glTF 2.0 metallic-roughness material (with the KHR_materials_emissive_strength factor folded in).</summary>
public sealed record GltfMaterial
{
    public string Name { get; init; } = "";
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;
    public GltfTextureRef? BaseColorTexture { get; init; }
    public float MetallicFactor { get; init; } = 1;
    public float RoughnessFactor { get; init; } = 1;
    public GltfTextureRef? MetallicRoughnessTexture { get; init; }
    /// <summary>Scale = normal scale.</summary>
    public GltfTextureRef? NormalTexture { get; init; }
    /// <summary>Scale = occlusion strength.</summary>
    public GltfTextureRef? OcclusionTexture { get; init; }
    public GltfTextureRef? EmissiveTexture { get; init; }
    public Vector3 EmissiveFactor { get; init; } = Vector3.Zero;
    public bool Blend { get; init; }
    public float? AlphaCutoff { get; init; }
    public bool DoubleSided { get; init; }

    public static GltfMaterial Default { get; } = new();
}

public sealed record GltfImage(ReadOnlyMemory<byte> Data, string? MimeType);

/// <summary>A run of triangles drawn with one material.</summary>
public sealed record GltfDraw(int FirstIndex, int IndexCount, int Material);

/// <summary>
/// A binary glTF flattened for drawing: every triangle primitive of the default scene, transformed
/// by its node hierarchy into one vertex and index list. The bounding box is computed like
/// Filament's <c>FilamentAsset::getBoundingBox</c>: the accessor min/max boxes of the primitives,
/// transformed into scene space.
/// </summary>
public sealed class GltfModel
{
    private GltfModel(SceneVertex[] vertices, uint[] indices, IReadOnlyList<GltfDraw> draws, IReadOnlyList<GltfMaterial> materials, IReadOnlyList<GltfImage> images, Vector3 min, Vector3 max)
    {
        Vertices = vertices;
        Indices = indices;
        Draws = draws;
        Materials = materials;
        Images = images;
        BoundsMin = min;
        BoundsMax = max;
    }

    public SceneVertex[] Vertices { get; }
    public uint[] Indices { get; }
    public IReadOnlyList<GltfDraw> Draws { get; }
    public IReadOnlyList<GltfMaterial> Materials { get; }
    public IReadOnlyList<GltfImage> Images { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public Vector3 Center => (BoundsMin + BoundsMax) / 2;
    public Vector3 HalfExtent => (BoundsMax - BoundsMin) / 2;

    private const int MaxVertices = 4_000_000;

    public static GltfModel ParseGlb(ReadOnlyMemory<byte> glb)
    {
        var data = glb.Span;
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546C67 || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 2)
            throw new InvalidDataException("Not a binary glTF 2.0 model.");
        int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[8..]), (uint)data.Length);
        ReadOnlyMemory<byte> json = default, bin = default;
        for (int pos = 12; pos + 8 <= length;)
        {
            int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (chunkLength < 0 || chunkLength > length - pos - 8) throw new InvalidDataException("Corrupt glTF chunk.");
            var chunk = glb.Slice(pos + 8, chunkLength);
            if (type == 0x4E4F534A && json.IsEmpty) json = chunk;
            else if (type == 0x004E4942 && bin.IsEmpty) bin = chunk;
            pos += 8 + ((chunkLength + 3) & ~3);
        }
        if (json.IsEmpty) throw new InvalidDataException("The glTF model has no JSON chunk.");
        using var doc = JsonDocument.Parse(json);
        return new Builder(doc.RootElement, bin).Build();
    }

    private sealed class Builder(JsonElement root, ReadOnlyMemory<byte> bin)
    {
        private readonly List<SceneVertex> _vertices = [];
        private readonly List<uint> _indices = [];
        private readonly List<GltfDraw> _draws = [];
        private Vector3 _min = new(float.MaxValue), _max = new(float.MinValue);

        public GltfModel Build()
        {
            var materials = Array(root, "materials").Select(ReadMaterial).ToList();
            var images = Array(root, "images").Select(ReadImage).ToList();
            var textures = Array(root, "textures").Select(t => Int(t, "source") ?? -1).ToList();
            // Texture references point at images from here on.
            materials = materials.Select(m => m with
            {
                BaseColorTexture = Resolve(m.BaseColorTexture, textures),
                MetallicRoughnessTexture = Resolve(m.MetallicRoughnessTexture, textures),
                NormalTexture = Resolve(m.NormalTexture, textures),
                OcclusionTexture = Resolve(m.OcclusionTexture, textures),
                EmissiveTexture = Resolve(m.EmissiveTexture, textures),
            }).ToList();

            var nodes = Array(root, "nodes").ToList();
            var scenes = Array(root, "scenes").ToList();
            int scene = Int(root, "scene") ?? 0;
            IEnumerable<int> roots = scene < scenes.Count
                ? Array(scenes[scene], "nodes").Select(n => n.GetInt32())
                : Enumerable.Range(0, nodes.Count).Except(nodes.SelectMany(n => Array(n, "children").Select(c => c.GetInt32())));
            var visited = new HashSet<int>();
            foreach (int node in roots) Visit(nodes, node, Matrix4x4.Identity, visited);
            if (_indices.Count == 0) throw new InvalidDataException("The glTF model has no triangles.");
            if (_min.X > _max.X)
            {
                _min = _vertices.Aggregate(new Vector3(float.MaxValue), (a, v) => Vector3.Min(a, v.Position));
                _max = _vertices.Aggregate(new Vector3(float.MinValue), (a, v) => Vector3.Max(a, v.Position));
            }
            return new GltfModel([.. _vertices], [.. _indices], _draws, materials, images, _min, _max);
        }

        private static GltfTextureRef? Resolve(GltfTextureRef? texture, List<int> textures) =>
            texture is { } t && t.Image >= 0 && t.Image < textures.Count && textures[t.Image] >= 0 ? t with { Image = textures[t.Image] } : null;

        private void Visit(List<JsonElement> nodes, int index, Matrix4x4 parent, HashSet<int> visited)
        {
            if (index < 0 || index >= nodes.Count || !visited.Add(index)) return;
            var node = nodes[index];
            var world = LocalMatrix(node) * parent;
            if (Int(node, "mesh") is { } mesh) AddMesh(mesh, world);
            foreach (var child in Array(node, "children")) Visit(nodes, child.GetInt32(), world, visited);
        }

        private static Matrix4x4 LocalMatrix(JsonElement node)
        {
            if (Floats(node, "matrix", 16) is { } m)
            {
                // glTF matrices are column-major; System.Numerics uses row vectors, i.e. the transpose.
                return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            }
            var t = Floats(node, "translation", 3) is { } tv ? new Vector3(tv[0], tv[1], tv[2]) : Vector3.Zero;
            var r = Floats(node, "rotation", 4) is { } rv ? Quaternion.Normalize(new Quaternion(rv[0], rv[1], rv[2], rv[3])) : Quaternion.Identity;
            var s = Floats(node, "scale", 3) is { } sv ? new Vector3(sv[0], sv[1], sv[2]) : Vector3.One;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        private void AddMesh(int meshIndex, Matrix4x4 world)
        {
            var meshes = Array(root, "meshes").ToList();
            if (meshIndex < 0 || meshIndex >= meshes.Count) return;
            Matrix4x4.Invert(world, out var inverse);
            var normalMatrix = Matrix4x4.Transpose(inverse);
            bool flip = world.GetDeterminant() < 0;
            foreach (var primitive in Array(meshes[meshIndex], "primitives"))
            {
                if ((Int(primitive, "mode") ?? 4) != 4 || !primitive.TryGetProperty("attributes", out var attributes)) continue;
                if (Int(attributes, "POSITION") is not { } positionAccessor) continue;
                var positions = ReadAccessor(positionAccessor, 3);
                int count = positions.Length / 3;
                if (count == 0 || _vertices.Count + count > MaxVertices) continue;
                var normals = Int(attributes, "NORMAL") is { } n ? ReadAccessor(n, 3) : null;
                var tangents = Int(attributes, "TANGENT") is { } t ? ReadAccessor(t, 4) : null;
                var uvs = Int(attributes, "TEXCOORD_0") is { } uv ? ReadAccessor(uv, 2) : null;
                uint[] indices = Int(primitive, "indices") is { } ia
                    ? ReadAccessor(ia, 1).Select(f => (uint)f).ToArray()
                    : Enumerable.Range(0, count).Select(i => (uint)i).ToArray();
                indices = indices.Take(indices.Length - indices.Length % 3).Where(i => i < count).ToArray();
                if (indices.Length % 3 != 0) continue;

                var local = new SceneVertex[count];
                for (int i = 0; i < count; i++)
                {
                    local[i].Position = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                    if (normals is not null && normals.Length >= (i + 1) * 3) local[i].Normal = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                    if (tangents is not null && tangents.Length >= (i + 1) * 4) local[i].Tangent = new Vector4(tangents[i * 4], tangents[i * 4 + 1], tangents[i * 4 + 2], tangents[i * 4 + 3]);
                    if (uvs is not null && uvs.Length >= (i + 1) * 2) local[i].Uv = new Vector2(uvs[i * 2], uvs[i * 2 + 1]);
                }
                if (normals is null) ComputeNormals(local, indices);
                if (tangents is null) ComputeTangents(local, indices);

                uint baseVertex = (uint)_vertices.Count;
                foreach (var v in local)
                {
                    var tangent = Vector3.Normalize(Vector3.TransformNormal(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), world));
                    _vertices.Add(new SceneVertex
                    {
                        Position = Vector3.Transform(v.Position, world),
                        Normal = SafeNormalize(Vector3.TransformNormal(v.Normal, normalMatrix)),
                        Tangent = new Vector4(float.IsFinite(tangent.X) ? tangent : Vector3.UnitX, (v.Tangent.W < 0 ? -1 : 1) * (flip ? -1 : 1)),
                        Uv = v.Uv,
                    });
                }
                int first = _indices.Count;
                for (int i = 0; i < indices.Length; i += 3)
                {
                    _indices.Add(baseVertex + indices[i]);
                    _indices.Add(baseVertex + indices[flip ? i + 2 : i + 1]);
                    _indices.Add(baseVertex + indices[flip ? i + 1 : i + 2]);
                }
                int material = Int(primitive, "material") ?? -1;
                if (_draws.Count > 0 && _draws[^1] is { } last && last.Material == material && last.FirstIndex + last.IndexCount == first)
                    _draws[^1] = last with { IndexCount = last.IndexCount + indices.Length };
                else
                    _draws.Add(new GltfDraw(first, indices.Length, material));
                ExtendBounds(positionAccessor, world);
            }
        }

        private void ExtendBounds(int accessorIndex, Matrix4x4 world)
        {
            var accessor = Array(root, "accessors").ElementAt(accessorIndex);
            if (Floats(accessor, "min", 3) is not { } lo || Floats(accessor, "max", 3) is not { } hi) return;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = Vector3.Transform(new Vector3((corner & 1) != 0 ? hi[0] : lo[0], (corner & 2) != 0 ? hi[1] : lo[1], (corner & 4) != 0 ? hi[2] : lo[2]), world);
                _min = Vector3.Min(_min, p);
                _max = Vector3.Max(_max, p);
            }
        }

        private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-20f ? Vector3.Normalize(v) : Vector3.UnitY;

        private static void ComputeNormals(SceneVertex[] vertices, uint[] indices)
        {
            for (int i = 0; i < indices.Length; i += 3)
            {
                ref var a = ref vertices[indices[i]];
                ref var b = ref vertices[indices[i + 1]];
                ref var c = ref vertices[indices[i + 2]];
                var n = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
                a.Normal += n;
                b.Normal += n;
                c.Normal += n;
            }
            for (int i = 0; i < vertices.Length; i++) vertices[i].Normal = SafeNormalize(vertices[i].Normal);
        }

        private static void ComputeTangents(SceneVertex[] vertices, uint[] indices)
        {
            var bitangents = new Vector3[vertices.Length];
            var tangents = new Vector3[vertices.Length];
            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                var e1 = vertices[i1].Position - vertices[i0].Position;
                var e2 = vertices[i2].Position - vertices[i0].Position;
                var d1 = vertices[i1].Uv - vertices[i0].Uv;
                var d2 = vertices[i2].Uv - vertices[i0].Uv;
                float r = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(r) < 1e-12f) continue;
                r = 1 / r;
                var t = (e1 * d2.Y - e2 * d1.Y) * r;
                var b = (e2 * d1.X - e1 * d2.X) * r;
                foreach (uint k in new[] { i0, i1, i2 })
                {
                    tangents[k] += t;
                    bitangents[k] += b;
                }
            }
            for (int i = 0; i < vertices.Length; i++)
            {
                var n = vertices[i].Normal;
                var t = tangents[i] - n * Vector3.Dot(n, tangents[i]);
                if (t.LengthSquared() < 1e-20f) t = MathF.Abs(n.X) < 0.9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY);
                t = Vector3.Normalize(t);
                float w = Vector3.Dot(Vector3.Cross(n, t), bitangents[i]) < 0 ? -1 : 1;
                vertices[i].Tangent = new Vector4(t, w);
            }
        }

        /// <summary>Reads an accessor as floats (normalised integers mapped to [0,1] / [-1,1]).</summary>
        private float[] ReadAccessor(int index, int expectedComponents)
        {
            var accessors = Array(root, "accessors").ToList();
            if (index < 0 || index >= accessors.Count) throw new InvalidDataException("Invalid glTF accessor.");
            var accessor = accessors[index];
            int count = Int(accessor, "count") ?? 0;
            int components = (accessor.TryGetProperty("type", out var type) ? type.GetString() : null) switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException("Unsupported glTF accessor type."),
            };
            if (components != expectedComponents) throw new InvalidDataException("Unexpected glTF accessor type.");
            int componentType = Int(accessor, "componentType") ?? 0;
            int size = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException("Unsupported glTF component type.") };
            bool normalized = accessor.TryGetProperty("normalized", out var norm) && norm.ValueKind == JsonValueKind.True;
            var result = new float[checked(count * components)];
            if (Int(accessor, "bufferView") is not { } viewIndex) return result; // all zeros (sparse-only accessors are not used by these models)
            var views = Array(root, "bufferViews").ToList();
            if (viewIndex < 0 || viewIndex >= views.Count) throw new InvalidDataException("Invalid glTF buffer view.");
            var view = views[viewIndex];
            if ((Int(view, "buffer") ?? 0) != 0) throw new InvalidDataException("External glTF buffers are not supported.");
            int offset = (Int(view, "byteOffset") ?? 0) + (Int(accessor, "byteOffset") ?? 0);
            int stride = Int(view, "byteStride") is { } s && s > 0 ? s : size * components;
            var span = bin.Span;
            if (count > 0 && (offset < 0 || (long)offset + (long)stride * (count - 1) + size * components > span.Length))
                throw new InvalidDataException("A glTF accessor exceeds its buffer.");
            for (int i = 0; i < count; i++)
            {
                var element = span[(offset + i * stride)..];
                for (int c = 0; c < components; c++)
                {
                    var b = element[(c * size)..];
                    result[i * components + c] = componentType switch
                    {
                        5126 => BinaryPrimitives.ReadSingleLittleEndian(b),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(b),
                        5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(b) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(b),
                        5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(b) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(b),
                        5121 => normalized ? b[0] / 255f : b[0],
                        _ => normalized ? Math.Max((sbyte)b[0] / 127f, -1) : (sbyte)b[0],
                    };
                }
            }
            return result;
        }

        private GltfImage ReadImage(JsonElement image)
        {
            string? mime = image.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
            if (Int(image, "bufferView") is { } viewIndex)
            {
                var view = Array(root, "bufferViews").ElementAt(viewIndex);
                int offset = Int(view, "byteOffset") ?? 0, length = Int(view, "byteLength") ?? 0;
                if (offset < 0 || length < 0 || offset + length > bin.Length) throw new InvalidDataException("A glTF image exceeds its buffer.");
                return new GltfImage(bin.Slice(offset, length), mime);
            }
            if (image.TryGetProperty("uri", out var uri) && uri.GetString() is { } text && text.StartsWith("data:", StringComparison.Ordinal) && text.IndexOf(";base64,", StringComparison.Ordinal) is int comma and > 0)
                return new GltfImage(Convert.FromBase64String(text[(comma + 8)..]), text[5..comma]);
            return new GltfImage(ReadOnlyMemory<byte>.Empty, mime);
        }

        private static GltfMaterial ReadMaterial(JsonElement m)
        {
            var pbr = m.TryGetProperty("pbrMetallicRoughness", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            bool hasPbr = pbr.ValueKind == JsonValueKind.Object;
            var baseColor = hasPbr && Floats(pbr, "baseColorFactor", 4) is { } bc ? new Vector4(bc[0], bc[1], bc[2], bc[3]) : Vector4.One;
            var emissive = Floats(m, "emissiveFactor", 3) is { } ef ? new Vector3(ef[0], ef[1], ef[2]) : Vector3.Zero;
            if (m.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("KHR_materials_emissive_strength", out var strength)
                && strength.TryGetProperty("emissiveStrength", out var es) && es.ValueKind == JsonValueKind.Number)
                emissive *= (float)es.GetDouble();
            return new GltfMaterial
            {
                Name = m.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                BaseColorFactor = baseColor,
                BaseColorTexture = hasPbr ? Texture(pbr, "baseColorTexture", null) : null,
                MetallicFactor = hasPbr ? Number(pbr, "metallicFactor") ?? 1 : 1,
                RoughnessFactor = hasPbr ? Number(pbr, "roughnessFactor") ?? 1 : 1,
                MetallicRoughnessTexture = hasPbr ? Texture(pbr, "metallicRoughnessTexture", null) : null,
                NormalTexture = Texture(m, "normalTexture", "scale"),
                OcclusionTexture = Texture(m, "occlusionTexture", "strength"),
                EmissiveTexture = Texture(m, "emissiveTexture", null),
                EmissiveFactor = emissive,
                Blend = m.TryGetProperty("alphaMode", out var mode) && mode.GetString() == "BLEND",
                AlphaCutoff = m.TryGetProperty("alphaMode", out var mask) && mask.GetString() == "MASK" ? Number(m, "alphaCutoff") ?? 0.5f : null,
                DoubleSided = m.TryGetProperty("doubleSided", out var ds) && ds.ValueKind == JsonValueKind.True,
            };
        }

        private static GltfTextureRef? Texture(JsonElement owner, string name, string? scaleName) =>
            owner.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object && Int(t, "index") is { } index
                ? new GltfTextureRef(index, scaleName is null ? 1 : Number(t, scaleName) ?? 1)
                : null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    private static float? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;

    private static float[]? Floats(JsonElement e, string name, int count)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < count) return null;
        var result = new float[count];
        int i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (i == count) break;
            result[i++] = item.ValueKind == JsonValueKind.Number ? (float)item.GetDouble() : 0;
        }
        return result;
    }
}
