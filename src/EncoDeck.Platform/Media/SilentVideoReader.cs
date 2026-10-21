// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace EncoDeck.Platform.Media;

/// <summary>
/// Decodes the pictures of a video file with the Media Foundation Source Reader and nothing else:
/// only the first video stream is selected, so audio is never read, decoded or rendered. There is no
/// media session, no audio endpoint, no System Media Transport Controls entry and nothing that media
/// keys or earbud taps (AVRCP) could pause — unlike <c>MediaPlayer</c>, which registers as media
/// playback even when muted.
/// <para>
/// Frames come out as 32-bit BGRA (MFVideoFormat_RGB32, converted by the reader's video processor)
/// with alpha forced to 255. Not thread-safe: use it from one thread.
/// </para>
/// </summary>
public sealed class SilentVideoReader : IDisposable
{
    // Media Foundation attribute and format GUIDs (mfapi.h / mfreadwrite.h).
    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MinimumDisplayApertureKey = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");

    private const int AllStreams = unchecked((int)0xFFFFFFFE);

    private static readonly Lock StartupLock = new();
    private static bool _started;

    private readonly IMFSourceReader _reader;
    private readonly int _stream;
    private int _pitch;
    private int _apertureX;
    private int _apertureY;

    private SilentVideoReader(IMFSourceReader reader, int stream, string pipeline)
    {
        _reader = reader;
        _stream = stream;
        Pipeline = pipeline;
    }

    /// <summary>Visible picture size in pixels (the display aperture of the output).</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>How the frames are produced (for diagnostics), e.g. "advanced+hardware".</summary>
    public string Pipeline { get; }

    /// <summary>Bytes of one BGRA frame as <see cref="ReadFrame"/> writes it.</summary>
    public int FrameBytes => Width * Height * 4;

    /// <summary>
    /// Opens <paramref name="path"/> and selects its first video stream only.
    /// </summary>
    /// <param name="maxHeight">
    /// When given and smaller than the video, frames are scaled down to it by the video processor
    /// (aspect kept), so less is copied per frame. Ignored where the processor cannot scale.
    /// </param>
    /// <exception cref="InvalidDataException">The file has no video stream.</exception>
    public static SilentVideoReader Open(string path, int? maxHeight = null)
    {
        EnsureStarted();
        // Advanced processing (with hardware decoders where present) first; the basic converter
        // (software, colour conversion only) where the advanced one is unavailable.
        Exception? last = null;
        foreach (bool advanced in new[] { true, false })
        {
            IMFSourceReader? reader = null;
            try
            {
                using var attributes = MediaFactory.MFCreateAttributes(2);
                if (advanced)
                {
                    attributes.Set(EnableAdvancedVideoProcessing, 1u);
                    attributes.Set(EnableHardwareTransforms, 1u);
                }
                else
                {
                    attributes.Set(EnableVideoProcessing, 1u);
                }
                reader = MediaFactory.MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes);
                int stream = FindFirstVideoStream(reader);
                // Nothing but this one video stream is ever read: audio stays deselected.
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(stream, true);

                var result = new SilentVideoReader(reader, stream, advanced ? "advanced+hardware" : "basic");
                result.ConfigureOutput(advanced ? maxHeight : null);
                return result;
            }
            catch (Exception ex) when (ex is SharpGenException or COMException)
            {
                reader?.Dispose();
                last = ex;
            }
        }
        throw new InvalidDataException($"Cannot decode video '{Path.GetFileName(path)}'.", last);
    }

    /// <summary>
    /// Decodes the next frame into <paramref name="bgra"/> (<see cref="FrameBytes"/> bytes, rows of
    /// <see cref="Width"/> × 4 bytes, top-down). False at the end of the stream.
    /// </summary>
    public bool ReadFrame(Span<byte> bgra, out TimeSpan timestamp)
    {
        if (bgra.Length < FrameBytes) throw new ArgumentException("Buffer too small.", nameof(bgra));
        while (true)
        {
            using var sample = _reader.ReadSample(_stream, SourceReaderControlFlag.None, out _, out SourceReaderFlag flags, out long time);
            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidDataException("The video decoder failed.");
            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0) ReadOutputType();
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                timestamp = TimeSpan.FromTicks(time);
                return false;
            }
            if (sample is null) continue; // stream tick (a gap): nothing to show
            timestamp = TimeSpan.FromTicks(time);
            Copy(sample, bgra);
            return true;
        }
    }

    /// <summary>
    /// Moves to <paramref name="position"/>. The next frames start at the key frame before it, so
    /// callers skip frames earlier than the target (<see cref="VideoSegment.Classify"/>).
    /// False when the position is outside the video (MF_E_INVALID_POSITION).
    /// </summary>
    public bool TrySeek(TimeSpan position)
    {
        try
        {
            _reader.SetCurrentPosition(Math.Max(0, position.Ticks));
            return true;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    public void Dispose() => _reader.Dispose();

    private static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started) return;
            // Once per process and never shut down: other readers may still be in use.
            MediaFactory.MFStartup(true).CheckError();
            _started = true;
        }
    }

    private static int FindFirstVideoStream(IMFSourceReader reader)
    {
        for (int index = 0; index < 64; index++)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType(index, 0);
            }
            catch (SharpGenException)
            {
                break; // no more streams
            }
            using (type)
            {
                if (type.GetGUID(MajorTypeKey) == VideoMajorType) return index;
            }
        }
        throw new InvalidDataException("No video stream.");
    }

    /// <summary>Asks for RGB32 (optionally scaled down) and reads back what the reader settled on.</summary>
    private void ConfigureOutput(int? maxHeight)
    {
        (int nativeWidth, int nativeHeight) = NativeSize();
        bool scaled = false;
        if (maxHeight is { } max && max > 0 && nativeHeight > 0 && max < nativeHeight * 0.75)
        {
            int height = max & ~1;
            int width = (int)Math.Round((double)nativeWidth * height / nativeHeight) & ~1;
            scaled = TrySetOutput(width, height);
        }
        if (!scaled && !TrySetOutput(null, null)) throw new InvalidDataException("RGB32 output is not available.");
        ReadOutputType();
    }

    private (int Width, int Height) NativeSize()
    {
        using var native = _reader.GetNativeMediaType(_stream, 0);
        ulong size = native.GetUInt64(FrameSizeKey);
        return ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
    }

    private bool TrySetOutput(int? width, int? height)
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MajorTypeKey, VideoMajorType);
        type.Set(SubtypeKey, Rgb32);
        if (width is { } w && height is { } h) type.Set(FrameSizeKey, ((ulong)(uint)w << 32) | (uint)h);
        try
        {
            _reader.SetCurrentMediaType(_stream, type);
            return true;
        }
        catch (SharpGenException) when (width is not null)
        {
            return false;
        }
    }

    private void ReadOutputType()
    {
        using var type = _reader.GetCurrentMediaType(_stream);
        ulong size = type.GetUInt64(FrameSizeKey);
        int frameWidth = (int)(size >> 32);
        int frameHeight = (int)(size & 0xFFFFFFFF);
        // Default stride; the 2D buffer's own pitch wins when the sample has one.
        int stride = frameWidth * 4;
        try
        {
            stride = unchecked((int)type.GetUInt32(DefaultStrideKey));
        }
        catch (SharpGenException)
        {
        }
        _pitch = stride;
        // Only the display aperture is shown (codecs pad the coded size, e.g. 1080 → 1088 rows).
        (_apertureX, _apertureY, Width, Height) = (0, 0, frameWidth, frameHeight);
        try
        {
            byte[] area = type.GetBlob(MinimumDisplayApertureKey);
            if (area.Length >= 16)
            {
                // MFVideoArea: MFOffset x { WORD fract; short value }, MFOffset y, SIZE { cx, cy }.
                int x = BitConverter.ToInt16(area, 2);
                int y = BitConverter.ToInt16(area, 6);
                int cx = BitConverter.ToInt32(area, 8);
                int cy = BitConverter.ToInt32(area, 12);
                if (x >= 0 && y >= 0 && cx > 0 && cy > 0 && x + cx <= frameWidth && y + cy <= frameHeight)
                {
                    (_apertureX, _apertureY, Width, Height) = (x, y, cx, cy);
                }
            }
        }
        catch (SharpGenException)
        {
        }
    }

    /// <summary>Copies the visible rows into <paramref name="bgra"/> and makes every pixel opaque.</summary>
    private unsafe void Copy(IMFSample sample, Span<byte> bgra)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        nint scan0;
        int pitch;
        bool locked2D = false;
        if (buffer2D is not null)
        {
            buffer2D.Lock2D(out scan0, out pitch);
            locked2D = true;
        }
        else
        {
            buffer.Lock(out scan0, out _, out _);
            // A negative default stride means bottom-up rows.
            pitch = _pitch;
            if (pitch < 0) scan0 += (nint)(-pitch) * (Height + _apertureY - 1);
        }
        try
        {
            int row = Width * 4;
            byte* source = (byte*)scan0 + (nint)_apertureY * pitch + _apertureX * 4;
            fixed (byte* target = bgra)
            {
                for (int y = 0; y < Height; y++)
                {
                    uint* s = (uint*)(source + (nint)y * pitch);
                    uint* d = (uint*)(target + (nint)y * row);
                    // RGB32's fourth byte is undefined: force alpha to 255.
                    for (int x = 0; x < Width; x++) d[x] = s[x] | 0xFF000000u;
                }
            }
        }
        finally
        {
            if (locked2D) buffer2D!.Unlock2D();
            else buffer.Unlock();
        }
    }
}
