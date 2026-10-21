// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>
/// Draws a <see cref="SceneAssets"/> scene with Direct3D 11 the way HeyMelody's Filament viewer does
/// (see <see cref="SceneShaders"/> and <see cref="OrbitCamera"/>): MSAA, transparent background,
/// camera exposure f/16, 1/125 s, ISO 100. Targets are a composition swap chain (<see cref="SwapChainTarget"/>)
/// or an off-screen texture (<see cref="RenderToPixels"/>, for diagnostics and tests).
/// <para>Not thread-safe: create, draw and dispose on one thread.</para>
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    public const Format ColorFormat = Format.B8G8R8A8_UNorm;
    private const Format DepthFormat = Format.D32_Float;

    /// <summary>Filament's default camera exposure: 1 / (1.2 · 2^EV100) with EV100 = log2(16² / (1/125) · 100/100).</summary>
    public static readonly float Exposure = (float)(1 / (1.2 * (16.0 * 16.0 / 0.008)));

    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _frameBuffer;
    private readonly ID3D11Buffer _materialBuffer;
    private readonly ID3D11SamplerState _materialSampler;
    private readonly ID3D11SamplerState _environmentSampler;
    private readonly ID3D11RasterizerState _cullBack;
    private readonly ID3D11RasterizerState _cullNone;
    private readonly ID3D11DepthStencilState _depthWrite;
    private readonly ID3D11DepthStencilState _depthRead;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11BlendState _premultiplied;
    private SceneGpu? _scene;
    private ID3D11Texture2D? _msaaColor;
    private ID3D11RenderTargetView? _msaaView;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;
    private int _targetWidth, _targetHeight;

    private SceneRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        Device = device;
        Context = context;
        SampleCount = PickSampleCount(device);

        var vs = Compile("VS", "vs_5_0");
        var ps = Compile("PS", "ps_5_0");
        _vertexShader = Own(device.CreateVertexShader(vs.Span));
        _pixelShader = Own(device.CreatePixelShader(ps.Span));
        _inputLayout = Own(device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 24, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
        ], vs.Span));
        _frameBuffer = Own(device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write)));
        _materialBuffer = Own(device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<MaterialConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write)));
        _materialSampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap, 0, 8, ComparisonFunction.Never, 0, float.MaxValue)));
        _environmentSampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)));
        // glTF triangles are counter-clockwise; the right-handed projection keeps that on screen.
        _cullBack = Own(device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true, MultisampleEnable = true, AntialiasedLineEnable = false }));
        _cullNone = Own(device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true, MultisampleEnable = true }));
        _depthWrite = Own(device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual)));
        _depthRead = Own(device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual)));
        _opaque = Own(device.CreateBlendState(BlendDescription.Opaque));
        _premultiplied = Own(device.CreateBlendState(BlendDescription.AlphaBlend));
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public uint SampleCount { get; }
    public bool HasScene => _scene is not null;

    /// <summary>Creates a hardware device (BGRA support, for composition), or WARP when <paramref name="software"/>.</summary>
    public static SceneRenderer Create(bool software = false)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(null, software ? DriverType.Warp : DriverType.Hardware, flags, levels, out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Failure && !software)
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out device, out context);
        result.CheckError();
        return new SceneRenderer(device!, context!);
    }

    private static uint PickSampleCount(ID3D11Device device)
    {
        foreach (uint count in new uint[] { 4, 2 })
        {
            if (device.CheckMultisampleQualityLevels(ColorFormat, count) > 0) return count;
        }
        return 1;
    }

    private static ReadOnlyMemory<byte> Compile(string entry, string profile)
    {
        var result = Compiler.Compile(SceneShaders.Source, null!, null!, entry, "EncoDeckScene.hlsl", profile,
            ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor, EffectFlags.None, out Blob? blob, out Blob? errors);
        try
        {
            if (result.Failure || blob is null)
                throw new InvalidOperationException("Shader compilation failed: " + (errors?.AsString() ?? result.ToString()));
            return blob.AsBytes();
        }
        finally
        {
            blob?.Dispose();
            errors?.Dispose();
        }
    }

    private T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }

    // ---- scene ----------------------------------------------------------------------------------

    /// <summary>Uploads the scene (replacing the previous one).</summary>
    public void Load(SceneAssets assets)
    {
        _scene?.Dispose();
        _scene = null;
        _scene = new SceneGpu(this, assets);
    }

    public void UnloadScene()
    {
        _scene?.Dispose();
        _scene = null;
    }

    // ---- drawing --------------------------------------------------------------------------------

    /// <summary>Draws into <paramref name="target"/> (a single-sample render target of <see cref="ColorFormat"/>).</summary>
    public void Render(ID3D11Texture2D target, int width, int height, OrbitCamera camera)
    {
        EnsureTargets(width, height);
        var context = Context;
        context.ClearRenderTargetView(_msaaView!, new Color4(0, 0, 0, 0));
        context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 1, 0);
        if (_scene is { } scene)
        {
            context.OMSetRenderTargets(_msaaView!, _depthView);
            context.RSSetViewport(new Viewport(0, 0, width, height));
            scene.Draw(camera, (float)width / height);
        }
        context.ResolveSubresource(target, 0, _msaaColor!, 0, ColorFormat);
        context.UnsetRenderTargets();
    }

    /// <summary>Renders one frame off screen and returns premultiplied BGRA pixels (diagnostics, tests).</summary>
    public byte[] RenderToPixels(int width, int height, OrbitCamera camera)
    {
        using var target = Device.CreateTexture2D(new Texture2DDescription(ColorFormat, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
        Render(target, width, height, camera);
        using var staging = Device.CreateTexture2D(new Texture2DDescription(ColorFormat, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        Context.CopyResource(staging, target);
        var mapped = Context.Map(staging, 0, MapMode.Read);
        try
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), pixels, y * width * 4, width * 4);
            return pixels;
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    private void EnsureTargets(int width, int height)
    {
        if (_msaaColor is not null && width == _targetWidth && height == _targetHeight) return;
        ReleaseTargets();
        _targetWidth = width;
        _targetHeight = height;
        var sample = new SampleDescription(SampleCount, 0);
        _msaaColor = Device.CreateTexture2D(new Texture2DDescription(ColorFormat, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, SampleCount, 0));
        _msaaView = Device.CreateRenderTargetView(_msaaColor);
        _depth = Device.CreateTexture2D(new Texture2DDescription(DepthFormat, (uint)width, (uint)height, 1, 1, BindFlags.DepthStencil, ResourceUsage.Default, CpuAccessFlags.None, SampleCount, 0));
        _depthView = Device.CreateDepthStencilView(_depth, new DepthStencilViewDescription(SampleCount > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D, DepthFormat));
        _ = sample;
    }

    /// <summary>Frees the size-dependent targets (e.g. while hidden).</summary>
    public void ReleaseTargets()
    {
        _msaaView?.Dispose();
        _msaaColor?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _msaaView = null;
        _msaaColor = null;
        _depthView = null;
        _depth = null;
        _targetWidth = _targetHeight = 0;
    }

    public void Dispose()
    {
        _scene?.Dispose();
        _scene = null;
        ReleaseTargets();
        Context.ClearState();
        Context.Flush();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
        Context.Dispose();
        Device.Dispose();
    }

    // ---- constant buffer layouts (must match SceneShaders) -----------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProjection;
        public Matrix4x4 IblRotation;
        public Vector4 CameraPosition;
        public Vector4 Sh0, Sh1, Sh2, Sh3, Sh4, Sh5, Sh6, Sh7, Sh8;
        public Vector4 LightDirection;
        public Vector4 LightColor;
        public Vector4 Params;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialConstants
    {
        public Vector4 BaseColorFactor;
        public Vector4 EmissiveFactor;
        public Vector4 Pbr;
        public Vector4 Maps;
        public Vector4 Extra;
    }

    private static void Upload<T>(ID3D11DeviceContext context, ID3D11Buffer buffer, in T value) where T : unmanaged
    {
        var mapped = context.Map(buffer, 0, MapMode.WriteDiscard);
        try
        {
            unsafe { *(T*)mapped.DataPointer = value; }
        }
        finally
        {
            context.Unmap(buffer, 0);
        }
    }

    /// <summary>GPU resources of one loaded scene.</summary>
    private sealed class SceneGpu : IDisposable
    {
        private readonly SceneRenderer _renderer;
        private readonly SceneAssets _assets;
        private readonly ID3D11Buffer _vertices;
        private readonly ID3D11Buffer _indices;
        private readonly Dictionary<(int Image, bool Srgb), ID3D11ShaderResourceView> _textures = [];
        private readonly List<IDisposable> _resources = [];
        private readonly ID3D11ShaderResourceView? _environment;
        private readonly float _maxLod;
        private readonly Matrix4x4 _world;
        private readonly Matrix4x4 _iblRotation;
        private readonly Vector4[] _sh = new Vector4[9];

        public SceneGpu(SceneRenderer renderer, SceneAssets assets)
        {
            _renderer = renderer;
            _assets = assets;
            var device = renderer.Device;
            var model = assets.Model;
            _vertices = Track(device.CreateBuffer(model.Vertices, BindFlags.VertexBuffer, ResourceUsage.Immutable));
            _indices = Track(device.CreateBuffer(model.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable));
            _world = OrbitCamera.ModelTransform(model.Center, model.HalfExtent, assets.Config.ModelScale);
            var angle = assets.Config.IblAngle;
            // HeyMelody: Quaternion.fromEuler(x, y, z) (Unity order, Z then X then Y) → IndirectLight.setRotation.
            var rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Concatenate(
                Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Radians(angle[2])), Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(angle[0]))),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(angle[1]))));
            // Shaders turn world directions into environment directions: the inverse rotation.
            _iblRotation = Matrix4x4.Transpose(rotation);
            if (assets.Ibl is { } ibl)
            {
                _environment = CreateCubemap(device, ibl);
                _maxLod = ibl.Levels.Length - 1;
                for (int i = 0; i < 9; i++) _sh[i] = ibl.SphericalHarmonics is { } sh ? new Vector4(sh[i], 0) : Vector4.Zero;
                if (ibl.SphericalHarmonics is null) _sh[0] = new Vector4(0.5f, 0.5f, 0.5f, 0);
            }
            foreach (var material in model.Materials)
            {
                Texture(material.BaseColorTexture, true);
                Texture(material.MetallicRoughnessTexture, false);
                Texture(material.NormalTexture, false);
                Texture(material.OcclusionTexture, false);
                Texture(material.EmissiveTexture, true);
            }
        }

        private T Track<T>(T resource) where T : IDisposable
        {
            _resources.Add(resource);
            return resource;
        }

        private ID3D11ShaderResourceView? Texture(GltfTextureRef? reference, bool srgb)
        {
            if (reference is null || reference.Image < 0 || reference.Image >= _assets.Images.Count || _assets.Images[reference.Image] is not { } image) return null;
            if (_textures.TryGetValue((reference.Image, srgb), out var existing)) return existing;
            var device = _renderer.Device;
            var format = srgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm;
            int mips = 1 + (int)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height)));
            var texture = Track(device.CreateTexture2D(new Texture2DDescription(format, (uint)image.Width, (uint)image.Height, 1, (uint)mips,
                BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips)));
            _renderer.Context.UpdateSubresource(image.Bgra, texture, 0, (uint)(image.Width * 4), 0);
            var view = Track(device.CreateShaderResourceView(texture));
            _renderer.Context.GenerateMips(view);
            _textures[(reference.Image, srgb)] = view;
            return view;
        }

        private ID3D11ShaderResourceView CreateCubemap(ID3D11Device device, KtxCubemap ibl)
        {
            var format = ibl.Format switch
            {
                KtxPixelFormat.R11G11B10Float => Format.R11G11B10_Float,
                KtxPixelFormat.Rgba16Float => Format.R16G16B16A16_Float,
                _ => Format.R8G8B8A8_UNorm,
            };
            int levels = ibl.Levels.Length;
            var data = new SubresourceData[6 * levels];
            var handles = new List<GCHandle>();
            try
            {
                for (int face = 0; face < 6; face++)
                {
                    for (int level = 0; level < levels; level++)
                    {
                        var bytes = ibl.Levels[level][face];
                        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        handles.Add(handle);
                        int edge = Math.Max(1, ibl.Size >> level);
                        data[face * levels + level] = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(edge * ibl.BytesPerPixel));
                    }
                }
                var texture = Track(device.CreateTexture2D(new Texture2DDescription(format, (uint)ibl.Size, (uint)ibl.Size, 6, (uint)levels,
                    BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.TextureCube), data));
                return Track(device.CreateShaderResourceView(texture));
            }
            finally
            {
                foreach (var handle in handles) handle.Free();
            }
        }

        public void Draw(OrbitCamera camera, float aspect)
        {
            var context = _renderer.Context;
            var config = _assets.Config;
            var eye = camera.Eye;
            var light = config.LightDirection;
            var frame = new FrameConstants
            {
                World = _world,
                ViewProjection = camera.View * OrbitCamera.Projection(aspect),
                IblRotation = _iblRotation,
                CameraPosition = new Vector4(eye, 1),
                Sh0 = _sh[0], Sh1 = _sh[1], Sh2 = _sh[2], Sh3 = _sh[3], Sh4 = _sh[4], Sh5 = _sh[5], Sh6 = _sh[6], Sh7 = _sh[7], Sh8 = _sh[8],
                LightDirection = new Vector4(light[0], light[1], light[2], Math.Max(0, config.LightIntensity) * Exposure),
                // HeyMelody's sun: Colors.cct(6500 K), close to white in linear sRGB.
                LightColor = new Vector4(1f, 0.973f, 0.996f, 0),
                Params = new Vector4(config.IblIntensity * Exposure, _maxLod, _environment is null ? 0 : 1, 0),
            };
            Upload(context, _renderer._frameBuffer, frame);

            context.IASetInputLayout(_renderer._inputLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(0, _vertices, SceneVertex.Size, 0);
            context.IASetIndexBuffer(_indices, Format.R32_UInt, 0);
            context.VSSetShader(_renderer._vertexShader);
            context.PSSetShader(_renderer._pixelShader);
            context.VSSetConstantBuffer(0, _renderer._frameBuffer);
            context.PSSetConstantBuffer(0, _renderer._frameBuffer);
            context.PSSetConstantBuffer(1, _renderer._materialBuffer);
            context.PSSetSampler(0, _renderer._materialSampler);
            context.PSSetSampler(1, _renderer._environmentSampler);
            context.PSSetShaderResource(5, _environment!);

            // Opaque first; blended materials afterwards without depth writes, like Filament's transparent pass.
            foreach (bool blendPass in new[] { false, true })
            {
                foreach (var draw in _assets.Model.Draws)
                {
                    var material = draw.Material >= 0 && draw.Material < _assets.Model.Materials.Count ? _assets.Model.Materials[draw.Material] : GltfMaterial.Default;
                    bool blended = material.Blend && HasTransparency(material);
                    if (blended != blendPass) continue;
                    var overrides = draw.Material >= 0 && draw.Material < config.Materials.Count ? config.Materials[draw.Material] : null;
                    Bind(material, overrides, blended);
                    context.RSSetState(material.DoubleSided ? _renderer._cullNone : _renderer._cullBack);
                    context.OMSetBlendState(blended ? _renderer._premultiplied : _renderer._opaque);
                    context.OMSetDepthStencilState(blended ? _renderer._depthRead : _renderer._depthWrite);
                    context.DrawIndexed((uint)draw.IndexCount, (uint)draw.FirstIndex, 0);
                }
            }
        }

        /// <summary>A BLEND material whose colour is fully opaque draws like an opaque one (depth-correct).</summary>
        private bool HasTransparency(GltfMaterial material)
        {
            if (material.BaseColorFactor.W < 0.999f) return true;
            if (material.BaseColorTexture is not { } t || t.Image < 0 || t.Image >= _assets.Images.Count || _assets.Images[t.Image] is not { } image) return false;
            var pixels = image.Bgra;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] < 250) return true;
            }
            return false;
        }

        private void Bind(GltfMaterial material, VfxMaterialConfig? overrides, bool blended)
        {
            var context = _renderer.Context;
            var baseColor = Texture(material.BaseColorTexture, true);
            var metallicRoughness = Texture(material.MetallicRoughnessTexture, false);
            var normal = Texture(material.NormalTexture, false);
            var occlusion = Texture(material.OcclusionTexture, false);
            var emissive = Texture(material.EmissiveTexture, true);
            // The scene config's material parameters replace gltfio's values (MaterialConfig.applyMaterialInstance).
            var baseFactor = overrides?.Color("baseColorFactor") is { } bc ? new Vector4(bc[0], bc[1], bc[2], bc[3]) : material.BaseColorFactor;
            var emissiveFactor = overrides?.Color("emissiveFactor") is { } ef ? new Vector3(ef[0], ef[1], ef[2]) : material.EmissiveFactor;
            var constants = new MaterialConstants
            {
                BaseColorFactor = baseFactor,
                EmissiveFactor = new Vector4(emissiveFactor, 0),
                Pbr = new Vector4(
                    overrides?.Float("metallicFactor") ?? material.MetallicFactor,
                    overrides?.Float("roughnessFactor") ?? material.RoughnessFactor,
                    overrides?.Float("normalScale") ?? material.NormalTexture?.Scale ?? 1,
                    overrides?.Float("aoStrength") ?? material.OcclusionTexture?.Scale ?? 1),
                Maps = new Vector4(baseColor is null ? 0 : 1, metallicRoughness is null ? 0 : 1, normal is null ? 0 : 1, occlusion is null ? 0 : 1),
                // Filament's default reflectance is 0.5 (4 % at normal incidence).
                Extra = new Vector4(emissive is null ? 0 : 1, overrides?.Float("reflectance") ?? 0.5f, material.AlphaCutoff ?? -1, blended ? 1 : 0),
            };
            Upload(context, _renderer._materialBuffer, constants);
            context.PSSetShaderResource(0, baseColor!);
            context.PSSetShaderResource(1, metallicRoughness!);
            context.PSSetShaderResource(2, normal!);
            context.PSSetShaderResource(3, occlusion!);
            context.PSSetShaderResource(4, emissive!);
        }

        private static float Radians(float degrees) => degrees * MathF.PI / 180;

        public void Dispose()
        {
            for (int i = _resources.Count - 1; i >= 0; i--) _resources[i].Dispose();
            _resources.Clear();
            _textures.Clear();
        }
    }
}
