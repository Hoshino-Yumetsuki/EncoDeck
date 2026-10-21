// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using EncoDeck.Platform.Media.Scene3D;

namespace EncoDeck.Tests.Platform;

public sealed class Scene3DTests
{
    // ---- synthetic files ----------------------------------------------------------------------

    /// <summary>A binary glTF with one triangle under a node translated by (10, 0, 0) and scaled by 2.</summary>
    private static byte[] Glb(object? extraNode = null, string alphaMode = "OPAQUE")
    {
        var bin = new byte[36 + 6 * 2 + 2];
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        for (int i = 0; i < positions.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 4), positions[i]);
        ushort[] indices = [0, 1, 2];
        for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(36 + i * 2), indices[i]);
        var nodes = new List<object> { new { mesh = 0, translation = new[] { 10f, 0, 0 }, scale = new[] { 2f, 2, 2 } } };
        if (extraNode is not null) nodes.Add(extraNode);
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
            nodes,
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 }, indices = 1, material = 0 } } } },
            materials = new[] { new { name = "1001", alphaMode, pbrMetallicRoughness = new { metallicFactor = 0.25, roughnessFactor = 0.5 }, emissiveFactor = new[] { 1f, 1, 1 } } },
            accessors = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0f, 0, 0 }, max = new[] { 1f, 1, 0 } },
                new { bufferView = 1, componentType = 5123, count = 3, type = "SCALAR" },
            },
            bufferViews = new[] { new { buffer = 0, byteOffset = 0, byteLength = 36 }, new { buffer = 0, byteOffset = 36, byteLength = 6 } },
            buffers = new[] { new { byteLength = bin.Length } },
        });
        int jsonLength = (json.Length + 3) & ~3;
        var glb = new byte[12 + 8 + jsonLength + 8 + bin.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        glb.AsSpan(20, jsonLength).Fill((byte)' ');
        json.CopyTo(glb, 20);
        int binAt = 20 + jsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binAt), (uint)bin.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binAt + 4), 0x004E4942);
        bin.CopyTo(glb, binAt + 8);
        return glb;
    }

    /// <summary>A Filament-style IBL: R11G11B10F cubemap with the "sh" key.</summary>
    private static byte[] Ktx(int size = 4, int mips = 2, bool withSh = true)
    {
        var kv = new List<byte>();
        if (withSh)
        {
            var pair = Encoding.ASCII.GetBytes("sh\0" + string.Concat(Enumerable.Range(0, 9).Select(i => $"{i * 0.1:0.0} {i * 0.1:0.0} {i * 0.1:0.0}\n")) + "\0");
            kv.AddRange(BitConverter.GetBytes(pair.Length));
            kv.AddRange(pair);
            while (kv.Count % 4 != 0) kv.Add(0);
        }
        var file = new List<byte>();
        file.AddRange([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (uint v in new uint[] { 0x04030201, 0x8C3A, 1, 0x1907, 0x8C3A, 0x8C3A, (uint)size, (uint)size, 0, 0, 6, (uint)mips, (uint)kv.Count })
            file.AddRange(BitConverter.GetBytes(v));
        file.AddRange(kv);
        for (int level = 0; level < mips; level++)
        {
            int edge = Math.Max(1, size >> level);
            file.AddRange(BitConverter.GetBytes(edge * edge * 4));
            for (int face = 0; face < 6; face++) file.AddRange(Enumerable.Repeat((byte)(face + 1), edge * edge * 4));
        }
        return [.. file];
    }

    private static byte[] Vfxms(string config, byte[] glb, byte[] ibl)
    {
        var json = Encoding.UTF8.GetBytes(config);
        var file = new byte[VfxScene.HeaderSize + json.Length + glb.Length + ibl.Length];
        int at = VfxScene.HeaderSize;
        int[] header = [at, json.Length, at + json.Length, glb.Length, at + json.Length + glb.Length, ibl.Length, 0, 0, 0, 0];
        for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(i * 4), header[i]);
        json.CopyTo(file, at);
        glb.CopyTo(file, at + json.Length);
        ibl.CopyTo(file, at + json.Length + glb.Length);
        return file;
    }

    private const string Config = """
        {"name":"Kapler","aaType":1,"enableSkyBox":false,"enableBloom":true,"enableSSAO":true,"enableShadow":true,"backgroundColor":[1,1,1,1],
         "materials":{"materials":[{"name":"1001","property":[
            {"name":"metallicFactor","value":1,"color":[1,1,1,1],"type":4},
            {"name":"baseColorFactor","value":0,"color":[0.5,0.25,1,1],"type":7},
            {"name":"reflectance","value":1,"color":[1,1,1,1],"type":4},
            {"name":"ignored","value":3,"color":[1,1,1,1],"type":0}]}]},
         "modelScale":1,"light":{"intensity":0,"direction":[0,-1,0],"iblIntensity":30000,"iblAngle":[0,90,0]}}
        """;

    // ---- container and config -----------------------------------------------------------------

    [Fact]
    public void VfxmsSectionsAndConfigAreReadLikeHeyMelody()
    {
        var scene = VfxScene.Parse(Vfxms(Config, Glb(), Ktx()));
        Assert.Equal("Kapler", scene.Config.Name);
        Assert.False(scene.Config.EnableSkybox);
        Assert.Equal(0, scene.Config.LightIntensity);
        Assert.Equal(30000, scene.Config.IblIntensity);
        Assert.Equal([0f, 90, 0], scene.Config.IblAngle);
        var material = Assert.Single(scene.Config.Materials);
        Assert.Equal(1, material.Float("metallicFactor"));
        Assert.Equal(1, material.Float("reflectance"));
        Assert.Equal([0.5f, 0.25f, 1, 1], material.Color("baseColorFactor")!);
        Assert.Null(material.Float("ignored")); // only FLOAT and FLOAT4 parameters are applied
        Assert.False(scene.Glb.IsEmpty);
        Assert.False(scene.Ibl.IsEmpty);
        Assert.True(scene.Skybox.IsEmpty);
    }

    [Fact]
    public void MissingConfigKeysUseHeyMelodyDefaults()
    {
        var config = VfxSceneConfig.Parse("""{"enableSkyBox":true}"""u8);
        Assert.Equal(100_000, config.LightIntensity);
        Assert.Equal(30_000, config.IblIntensity);
        Assert.Equal([0f, -1, 0], config.LightDirection);
        Assert.Equal(1, config.ModelScale);
    }

    [Fact]
    public void CorruptContainersAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => VfxScene.Parse(new byte[12]));
        var file = Vfxms(Config, Glb(), Ktx());
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(12), int.MaxValue); // glb length beyond the file
        Assert.Throws<InvalidDataException>(() => VfxScene.Parse(file));
    }

    // ---- glTF ---------------------------------------------------------------------------------

    [Fact]
    public void GltfTrianglesAreFlattenedIntoSceneSpaceWithFilamentBounds()
    {
        var model = GltfModel.ParseGlb(Glb());
        Assert.Equal(3, model.Vertices.Length);
        Assert.Equal([0u, 1, 2], model.Indices);
        Assert.Equal(new Vector3(12, 0, 0), model.Vertices[1].Position);
        Assert.Equal(new Vector3(10, 2, 0), model.Vertices[2].Position);
        // Generated normal faces +Z (counter-clockwise winding), tangent along +X.
        Assert.True(Vector3.Distance(Vector3.UnitZ, model.Vertices[0].Normal) < 1e-5);
        Assert.Equal(new Vector3(10, 0, 0), model.BoundsMin);
        Assert.Equal(new Vector3(12, 2, 0), model.BoundsMax);
        var material = Assert.Single(model.Materials);
        Assert.Equal(0.25f, material.MetallicFactor);
        Assert.Equal(0.5f, material.RoughnessFactor);
        Assert.Equal(Vector3.One, material.EmissiveFactor);
        Assert.Equal(new GltfDraw(0, 3, 0), Assert.Single(model.Draws));
    }

    [Fact]
    public void MirroredNodesKeepTheirTrianglesFrontFacing()
    {
        var model = GltfModel.ParseGlb(Glb(new { mesh = 0, scale = new[] { -1f, 1, 1 } }));
        Assert.Equal(6, model.Indices.Length);
        // Second copy is mirrored in X: winding is flipped so its face still points at its normal.
        var v = model.Vertices;
        uint a = model.Indices[3], b = model.Indices[4], c = model.Indices[5];
        var faceNormal = Vector3.Cross(v[b].Position - v[a].Position, v[c].Position - v[a].Position);
        Assert.True(Vector3.Dot(faceNormal, v[a].Normal) > 0);
    }

    [Fact]
    public void NotAGlbIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => GltfModel.ParseGlb(new byte[64]));
    }

    // ---- KTX ----------------------------------------------------------------------------------

    [Fact]
    public void FilamentIblCubemapAndSphericalHarmonicsAreRead()
    {
        var ibl = KtxCubemap.Parse(Ktx(size: 4, mips: 2));
        Assert.Equal(KtxPixelFormat.R11G11B10Float, ibl.Format);
        Assert.Equal(4, ibl.Size);
        Assert.Equal(2, ibl.Levels.Length);
        Assert.Equal(4 * 4 * 4, ibl.Levels[0][5].Length);
        Assert.All(ibl.Levels[1][2], b => Assert.Equal(3, b)); // +Y face of level 1
        Assert.NotNull(ibl.SphericalHarmonics);
        Assert.Equal(new Vector3(0.8f), ibl.SphericalHarmonics![8]);
        Assert.Null(KtxCubemap.Parse(Ktx(withSh: false)).SphericalHarmonics);
    }

    // ---- camera -------------------------------------------------------------------------------

    [Fact]
    public void CameraStartsLikeHeyMelodyAndTurnsByItself()
    {
        var camera = new OrbitCamera();
        Assert.Equal(new Vector3(0, 0, 1), camera.Eye, new Vector3Comparer());
        for (int i = 0; i < 60; i++) camera.Update(1 / 60f);
        Assert.InRange(camera.Yaw, 19, 21); // 20°/s
        camera.AutoRotate = false;
        float yaw = camera.Yaw;
        for (int i = 0; i < 60; i++) camera.Update(1 / 60f);
        Assert.Equal(yaw, camera.Yaw);
        Assert.False(camera.IsMoving);
    }

    [Fact]
    public void DraggingTurnsInBothAxesWithClampedPitchAndInertia()
    {
        var camera = new OrbitCamera { AutoRotate = false };
        camera.BeginControl();
        for (int i = 0; i < 30; i++)
        {
            camera.Slide(4, 30); // right and down
            camera.Update(1 / 60f);
        }
        Assert.True(camera.Yaw < 0, "dragging right turns the camera to the left (the model to the right)");
        Assert.Equal(-85, camera.Pitch); // looking down from above, clamped like HeyMelody
        Assert.True(camera.Eye.Y > OrbitCamera.Center.Y);
        camera.EndControl();
        float before = camera.Yaw;
        camera.Update(1 / 60f);
        Assert.True(camera.Yaw < before, "keeps coasting after release");
        for (int i = 0; i < 60 * 20; i++) camera.Update(1 / 60f);
        Assert.Equal(OrbitCamera.State.Rotate, camera.Current);
        Assert.InRange(camera.Pitch, -0.01f, 0.01f); // eased back to level
    }

    [Fact]
    public void ZoomStaysWithinHeyMelodyLimits()
    {
        var camera = new OrbitCamera();
        for (int i = 0; i < 100; i++) camera.Scroll(400);
        camera.EndControl();
        for (int i = 0; i < 30; i++) camera.Update(1 / 60f);
        Assert.InRange(camera.Distance, camera.MinDistance - 0.01f, camera.MinDistance + 0.2f);
        Assert.Equal(3.75f, camera.MinDistance, 3);
        Assert.Equal(7.5f, camera.MaxDistance, 3);
    }

    [Fact]
    public void ModelIsScaledToItsLargestHalfExtentAndCentredOnTheOrbit()
    {
        var transform = OrbitCamera.ModelTransform(new Vector3(10, 20, 30), new Vector3(4, 2, 1), modelScale: 1);
        Assert.Equal(OrbitCamera.Center, Vector3.Transform(new Vector3(10, 20, 30), transform), new Vector3Comparer());
        Assert.Equal(OrbitCamera.Center + new Vector3(1, 0, 0), Vector3.Transform(new Vector3(14, 20, 30), transform), new Vector3Comparer());
    }

    // ---- renderer (WARP, no window) -------------------------------------------------------------

    [Fact]
    public async Task RendererDrawsTheModelOnATransparentBackground()
    {
        var assets = await SceneAssets.LoadAsync(Vfxms(Config, Glb(), Ktx()));
        using var renderer = SceneRenderer.Create(software: true);
        renderer.Load(assets);
        var camera = OrbitCamera.ForView(0, 0);
        var pixels = renderer.RenderToPixels(64, 64, camera);
        Assert.Equal(0, pixels[3]); // corner: transparent
        int center = (40 * 64 + 24) * 4; // inside the lower-left half of the unit square the triangle spans
        Assert.Equal(255, pixels[center + 3]);
        Assert.True(pixels[center] + pixels[center + 1] + pixels[center + 2] > 0);
    }

    private sealed class Vector3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
