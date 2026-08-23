namespace ModernWigiDash.Widgets;

/// <summary>
/// The media-decode module: raw file bytes in, an installable
/// <see cref="MediaDecodeResult"/> out. Owns the decompression-bomb caps,
/// the multi-frame delta compositing (with the heap-corruption guard around
/// SKCodec.GetPixels + CopyTo), and the malformed-media rule: decode never
/// throws, a refusal or failure is <see cref="MediaDecodeResult.None"/>
/// with every partial bitmap disposed. The widget keeps load scheduling, the
/// version-token publish, and retirement; the decode itself is asserted
/// through this module without a widget instance or a file on disk.
/// </summary>
internal static class MediaDecoder
{
    // Decompression-bomb caps: refuse media whose raw byte size, frame count,
    // or decoded pixel footprint would exhaust memory.
    public const int MaxFileBytes = 32 * 1024 * 1024;
    public const int MaxFrames = 256;
    public const long MaxPixelsPerFrame = 4096L * 4096;
    public const long MaxTotalFrameBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Decodes one media file from its bytes under the bomb caps. Never
    /// throws for malformed media: a refusal or a decode failure is
    /// <see cref="MediaDecodeResult.None"/> (any partial bitmaps disposed),
    /// and <paramref name="logRefusal"/> receives the refusal facts when the
    /// media is refused (the caller owns the log vocabulary — the widget
    /// prefixes its own name).
    /// </summary>
    public static MediaDecodeResult Decode(byte[] data, Action<string>? logRefusal = null)
    {
        if (data.Length > MaxFileBytes)
        {
            logRefusal?.Invoke($"refusing {data.Length} byte media (cap {MaxFileBytes} bytes)");
            return MediaDecodeResult.None;
        }

        SKBitmap[]? frames = null;
        long[]? durations = null;
        SKBitmap? still = null;
        try
        {
            using var dataRef = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(dataRef);
            if (codec is not null && codec.FrameCount > 1)
            {
                if (!DecodeAnimated(codec, ref frames, ref durations, logRefusal))
                {
                    return MediaDecodeResult.None;
                }
            }
            else
            {
                still = DecodeStill(data, codec, logRefusal);
            }
        }
        catch
        {
            // Malformed media: never throw, and never hand back a partial
            // buffer - dispose what was decoded (the frame array is ref, so
            // a mid-loop throw still exposes the decoded prefix here), leave
            // a failure line, and report the empty result.
            if (frames is not null)
            {
                foreach (var frame in frames)
                {
                    frame?.Dispose();
                }
            }
            still?.Dispose();
            logRefusal?.Invoke($"decode failed ({data.Length} bytes)");
            return MediaDecodeResult.None;
        }

        return new MediaDecodeResult(frames, durations, still);
    }

    /// <summary>Decodes a multi-frame codec under the bomb caps; false when
    /// the media is refused (the result then installs nothing). The frame
    /// arrays are allocated before the per-frame loop and filled through
    /// ref, so a mid-loop throw leaves the decoded prefix visible to the
    /// caller's catch (which disposes it) instead of orphaned.</summary>
    private static bool DecodeAnimated(SKCodec codec, ref SKBitmap[]? frames, ref long[]? durations, Action<string>? logRefusal)
    {
        if (codec.FrameCount > MaxFrames ||
            (long)codec.Info.Width * codec.Info.Height > MaxPixelsPerFrame ||
            (long)codec.Info.Width * codec.Info.Height * codec.FrameCount * 4L > MaxTotalFrameBytes)
        {
            logRefusal?.Invoke($"refusing {codec.Info.Width}x{codec.Info.Height}x{codec.FrameCount} decode (cap {MaxFrames} frames, {MaxPixelsPerFrame} px/frame, {MaxTotalFrameBytes} total bytes)");
            return false;
        }

        int frameCount = codec.FrameCount;
        SKImageInfo info = codec.Info;

        // Force RGBA8888 frame buffers: Skia composites frames with
        // PriorFrame in RGBA regardless of the codec's native format,
        // and writing 4 bytes/pixel into an Index8/palette-sized buffer
        // (codec.Info's natural format for GIFs) overruns the heap.
        SKImageInfo frameInfo = info.ColorType == SKColorType.Rgba8888
                    ? info
                    : info.WithColorType(SKColorType.Rgba8888);
        frames = new SKBitmap[frameCount];
        durations = new long[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
            var frame = new SKBitmap(frameInfo);

            // GIF frames are deltas: the codec needs the previous
            // frame's pixels already in the destination to composite
            // correctly. Without it, delta frames decode as mostly
            // empty/black regions and the animation flashes black.
            //
            // NOTE: CopyTo REPLACES the destination's pixel buffer
            // (draws into a temp, then Swap), so GetPixels must read
            // frame.GetPixels() AFTER the copy — a pointer captured
            // beforehand dangles (the buffer is freed) and the codec
            // writes into freed memory (heap corruption 0xc0000374).
            if (i > 0)
            {
                frames[i - 1].CopyTo(frame);
            }

            var options = new SKCodecOptions { FrameIndex = i, PriorFrame = i > 0 ? i - 1 : -1 };
            if (codec.GetPixels(frameInfo, frame.GetPixels(), options) != SKCodecResult.Success)
            {
                // Never install a garbage buffer: the replacement was
                // never composited from the prior frame, so re-seed it
                // from the previous frame's pixels (or erase frame 0)
                // to keep the fallback deterministic.
                frame.Dispose();
                frame = new SKBitmap(frameInfo);
                if (i > 0)
                {
                    frames[i - 1].CopyTo(frame);
                }
                else
                {
                    frame.Erase(new SKColor(0, 0, 0, 0));
                }
            }
            frames[i] = frame;
            durations[i] = codec.FrameInfo[i].Duration > 0 ? codec.FrameInfo[i].Duration : 100L;
        }

        return true;
    }

    /// <summary>Decodes a still image when within the per-frame pixel cap;
    /// null (with a refusal line) when refused, null (with a failure line)
    /// when nothing decodes.</summary>
    private static SKBitmap? DecodeStill(byte[] data, SKCodec? codec, Action<string>? logRefusal)
    {
        if (codec is not null && (long)codec.Info.Width * codec.Info.Height > MaxPixelsPerFrame)
        {
            logRefusal?.Invoke($"refusing {codec.Info.Width}x{codec.Info.Height} still image (cap {MaxPixelsPerFrame} px)");
            return null;
        }

        SKBitmap? bitmap = SKBitmap.Decode(data);
        if (bitmap is null)
        {
            logRefusal?.Invoke($"no image decoded ({data.Length} bytes)");
        }
        return bitmap;
    }
}
