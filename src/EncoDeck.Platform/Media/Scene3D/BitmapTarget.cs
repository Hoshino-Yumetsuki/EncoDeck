// SPDX-License-Identifier: GPL-3.0-only
using Vortice.Direct3D11;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>
/// Renders frames into premultiplied BGRA pixels for an ordinary XAML bitmap. Unlike a
/// <c>SwapChainPanel</c>, a bitmap is composed with the XAML beneath it, so transparent pixels show
/// the page (and its layer over Mica) rather than the window backdrop.
/// </summary>
public sealed class BitmapTarget : IDisposable
{
    private readonly SceneRenderer _renderer;
    private ID3D11Texture2D? _color;
    private ID3D11Texture2D? _staging;

    public BitmapTarget(SceneRenderer renderer, int width, int height)
    {
        _renderer = renderer;
        Resize(width, height);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Resizes the targets (physical pixels).</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_color is not null && width == Width && height == Height) return;
        ReleaseTextures();
        Width = width;
        Height = height;
        var device = _renderer.Device;
        _color = device.CreateTexture2D(new Texture2DDescription(SceneRenderer.ColorFormat, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
        _staging = device.CreateTexture2D(new Texture2DDescription(SceneRenderer.ColorFormat, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    /// <summary>Draws one frame into <paramref name="pixels"/> (<c>Width * Height * 4</c> bytes, rows packed).</summary>
    public void Render(OrbitCamera camera, Span<byte> pixels)
    {
        _renderer.Render(_color!, Width, Height, camera);
        var context = _renderer.Context;
        context.CopyResource(_staging!, _color!);
        var mapped = context.Map(_staging!, 0, MapMode.Read);
        try
        {
            int row = Width * 4;
            for (int y = 0; y < Height; y++)
            {
                unsafe
                {
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (nint)y * mapped.RowPitch, row).CopyTo(pixels.Slice(y * row, row));
                }
            }
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    private void ReleaseTextures()
    {
        _staging?.Dispose();
        _color?.Dispose();
        _staging = null;
        _color = null;
    }

    public void Dispose() => ReleaseTextures();
}
