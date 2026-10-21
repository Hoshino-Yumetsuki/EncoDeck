// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>
/// A composition swap chain (premultiplied alpha, flip model) for a XAML <c>SwapChainPanel</c>,
/// sized in physical pixels and scaled back to the panel's DIPs.
/// </summary>
public sealed class SwapChainTarget : IDisposable
{
    private readonly SceneRenderer _renderer;
    private float _scaleX = 1, _scaleY = 1;

    public SwapChainTarget(SceneRenderer renderer, int width, int height)
    {
        _renderer = renderer;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        using var dxgiDevice = renderer.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        SwapChain = factory.CreateSwapChainForComposition(renderer.Device, new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = SceneRenderer.ColorFormat,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
        });
    }

    public IDXGISwapChain1 SwapChain { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Resizes the buffers (physical pixels) and maps them onto the panel's DIPs.</summary>
    public void Resize(int width, int height, float scaleX, float scaleY)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width != Width || height != Height)
        {
            _renderer.Context.UnsetRenderTargets();
            _renderer.Context.Flush();
            SwapChain.ResizeBuffers(2, (uint)width, (uint)height, SceneRenderer.ColorFormat, SwapChainFlags.None).CheckError();
            Width = width;
            Height = height;
        }
        if (scaleX != _scaleX || scaleY != _scaleY)
        {
            _scaleX = scaleX;
            _scaleY = scaleY;
            using var swapChain2 = SwapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (swapChain2 is not null) swapChain2.MatrixTransform = Matrix3x2.CreateScale(1 / Math.Max(scaleX, 0.01f), 1 / Math.Max(scaleY, 0.01f));
        }
    }

    /// <summary>Draws one frame and presents it on the next vertical blank.</summary>
    public void Present(OrbitCamera camera)
    {
        using (var buffer = SwapChain.GetBuffer<ID3D11Texture2D>(0))
            _renderer.Render(buffer, Width, Height, camera);
        SwapChain.Present(1, PresentFlags.None).CheckError();
    }

    public void Dispose() => SwapChain.Dispose();
}
