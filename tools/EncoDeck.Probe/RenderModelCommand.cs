// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Platform.Media.Scene3D;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace EncoDeck.Probe;

/// <summary>Renders a HeyMelody 3D model (.vfxms) off screen to PNG files, without any window.</summary>
internal static class RenderModelCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        string? file = options.Get("file") ?? options.Positional.FirstOrDefault();
        string? output = options.Get("out");
        if (file is null || output is null)
        {
            Console.Error.WriteLine("Usage: render-model --file <model.vfxms> --out <folder> [--size 600] [--yaw 0,90,180] [--pitch 0] [--software]");
            return 2;
        }
        int size = options.GetInt("size", 600);
        float pitch = float.Parse(options.Get("pitch") ?? "0", CultureInfo.InvariantCulture);
        var yaws = (options.Get("yaw") ?? "0").Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        var clock = Stopwatch.StartNew();
        var assets = await SceneAssets.LoadAsync(file, ct);
        Console.WriteLine($"Loaded in {clock.ElapsedMilliseconds} ms: {assets.Model.Vertices.Length} vertices, {assets.Model.Indices.Length / 3} triangles, "
            + $"{assets.Model.Draws.Count} draws, {assets.Model.Materials.Count} materials, IBL {(assets.Ibl is null ? "none" : $"{assets.Ibl.Size}px x{assets.Ibl.Levels.Length} ({assets.Ibl.Format})")}, "
            + $"bounds {assets.Model.BoundsMin} - {assets.Model.BoundsMax}, config '{assets.Config.Name}' ibl {assets.Config.IblIntensity} angle [{string.Join(",", assets.Config.IblAngle)}] light {assets.Config.LightIntensity}");

        using var renderer = SceneRenderer.Create(options.Has("software"));
        clock.Restart();
        renderer.Load(assets);
        Console.WriteLine($"Uploaded in {clock.ElapsedMilliseconds} ms (MSAA x{renderer.SampleCount})");
        Directory.CreateDirectory(output);
        foreach (float yaw in yaws)
        {
            var camera = OrbitCamera.ForView(yaw, pitch);
            clock.Restart();
            var pixels = renderer.RenderToPixels(size, size, camera);
            string path = Path.GetFullPath(Path.Combine(output, $"{Path.GetFileNameWithoutExtension(file)}_yaw{yaw:0}_pitch{pitch:0}.png"));
            await SavePngAsync(path, size, size, pixels);
            Console.WriteLine($"{path} ({clock.ElapsedMilliseconds} ms)");
        }
        return 0;
    }

    private static async Task SavePngAsync(string path, int width, int height, byte[] premultipliedBgra)
    {
        File.WriteAllBytes(path, []);
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, premultipliedBgra);
        await encoder.FlushAsync();
    }
}
