// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EncoDeck.Platform.Media.Scene3D;

/// <summary>Decoded BGRA pixels of one material texture.</summary>
public sealed record SceneImage(int Width, int Height, byte[] Bgra);

/// <summary>
/// Everything the GPU needs for a <c>.vfxms</c> scene, prepared off the UI thread: the parsed config,
/// the flattened model, decoded textures (limited to <see cref="MaxTextureSize"/> to keep memory low)
/// and the IBL cubemap.
/// </summary>
public sealed class SceneAssets
{
    /// <summary>The model is shown a few hundred pixels tall; larger textures only cost memory.</summary>
    public const int MaxTextureSize = 1024;

    private SceneAssets(VfxSceneConfig config, GltfModel model, IReadOnlyList<SceneImage?> images, KtxCubemap? ibl)
    {
        Config = config;
        Model = model;
        Images = images;
        Ibl = ibl;
    }

    public VfxSceneConfig Config { get; }
    public GltfModel Model { get; }
    public IReadOnlyList<SceneImage?> Images { get; }
    public KtxCubemap? Ibl { get; }

    public static async Task<SceneAssets> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SceneAssets> LoadAsync(ReadOnlyMemory<byte> file, CancellationToken cancellationToken = default)
    {
        var scene = VfxScene.Parse(file);
        var model = GltfModel.ParseGlb(scene.Glb);
        KtxCubemap? ibl = null;
        if (!scene.Ibl.IsEmpty)
        {
            try { ibl = KtxCubemap.Parse(scene.Ibl.Span); }
            catch (InvalidDataException) { /* rendered without image-based light */ }
        }
        // Only images a material uses are decoded.
        var used = model.Materials.SelectMany(m => new[] { m.BaseColorTexture, m.MetallicRoughnessTexture, m.NormalTexture, m.OcclusionTexture, m.EmissiveTexture })
            .Where(t => t is not null).Select(t => t!.Image).ToHashSet();
        var images = new SceneImage?[model.Images.Count];
        await Task.WhenAll(used.Where(i => i >= 0 && i < images.Length).Select(async i =>
        {
            images[i] = await DecodeAsync(model.Images[i].Data, cancellationToken).ConfigureAwait(false);
        })).ConfigureAwait(false);
        return new SceneAssets(scene.Config, model, images, ibl);
    }

    private static async Task<SceneImage?> DecodeAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty) return null;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.ToArray().AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            uint width = decoder.PixelWidth, height = decoder.PixelHeight;
            double scale = Math.Min(1, (double)MaxTextureSize / Math.Max(width, height));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            // Straight alpha and no colour management: texel values stay exactly as authored.
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
            return new SceneImage((int)transform.ScaledWidth, (int)transform.ScaledHeight, pixels.DetachPixelData());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
