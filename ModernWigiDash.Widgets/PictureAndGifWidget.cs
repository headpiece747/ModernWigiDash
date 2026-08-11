using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("picture_viewer", "Picture & GIF Viewer", Category = "Social & Visual")]
public class PictureAndGifWidget : ModernWidgetBase
{
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Image Folder/File Path", WidgetPropertyType.Path, "Path to image or folder of images", "C:\\Pictures")]
    public string ImagePath { get; set; } = "C:\\Pictures";

    [WidgetProperty("Source Mode", WidgetPropertyType.Choice, "Auto detects file or folder; forces one mode when set", "Auto", "Auto", "Single Image", "Folder (Cycle)")]
    public string SourceMode { get; set; } = "Auto";

    [WidgetProperty("Fit Mode", WidgetPropertyType.Choice, "Aspect ratio scaling mode", "Cover", "Cover", "Contain", "Stretch")]
    public string FitMode { get; set; } = "Cover";

    [WidgetProperty("Corner Radius", WidgetPropertyType.Number, "Rounded corners radius", 16f)]
    public float CornerRadius { get; set; } = 16f;

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Placeholder icon and hint color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private string[] _folderImages = [];
    private bool _folderScanned;
    private int _imageIndex = 0;
    private SKBitmap? _staticBitmap;
    private SKBitmap[]? _gifFrames;
    private long[]? _gifFrameDurationsMs;
    private int _gifFrameIndex;
    private long _gifNextFrameTick;
    private string _loadedPath = "";
    private int _loadVersion;

    // Last-probed-path existence cache: File.Exists per frame is a filesystem
    // hit; the probe is re-done only when the path changes (ResetMedia clears
    // the key on ImagePath/SourceMode changes).
    private string _lastProbePath = "";
    private bool _lastProbeExists;

    private bool ProbeFileExists(string path)
    {
        if (path != _lastProbePath)
        {
            _lastProbePath = path;
            _lastProbeExists = File.Exists(path);
        }
        return _lastProbeExists;
    }

    // Media is decoded on a background thread and published atomically. The
    // render thread may be drawing a bitmap at publish time, so replaced
    // bitmaps are retired via _mediaRetirement and disposed only on the UI
    // render thread (start of Render) or widget teardown — never from the
    // decode task. _mediaLock guards the field swaps below (frames array,
    // frame index, static bitmap); the retirement list itself lives inside
    // RetiredBitmapSet.
    private readonly Lock _mediaLock = new();
    private readonly RetiredBitmapSet _mediaRetirement = new();

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (propertyName is nameof(ImagePath) or nameof(SourceMode))
        {
            ResetMedia();
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _mediaRetirement.DisposeRetired();

        string? currentFile = GetActiveImageFile();

        if (!string.IsNullOrEmpty(currentFile) && ProbeFileExists(currentFile))
        {
            if (currentFile != _loadedPath)
            {
                _loadedPath = currentFile;
                StartMediaLoad(currentFile);
            }

            SKBitmap[]? frames;
            SKBitmap? staticBitmap;
            int frameIndex;
            lock (_mediaLock)
            {
                frames = _gifFrames;
                staticBitmap = _staticBitmap;
                frameIndex = _gifFrameIndex;

                if (frames is { Length: > 1 } && Environment.TickCount64 >= _gifNextFrameTick)
                {
                    frameIndex = (_gifFrameIndex + 1) % frames.Length;
                    _gifFrameIndex = frameIndex;
                    _gifNextFrameTick = Environment.TickCount64 + FrameDurationMs(_gifFrameDurationsMs, frameIndex);
                }
            }

            if (frames is { Length: > 1 })
            {
                // frameIndex was computed against this array under the lock;
                // even if a publish swaps _gifFrames now, the old array (and its
                // bitmaps) stays alive until the next Render's dispose pass.
                DrawImage(canvas, bounds, frames[frameIndex]);
                return;
            }

            if (staticBitmap != null)
            {
                DrawImage(canvas, bounds, staticBitmap);
                return;
            }
        }

        DrawPlaceholder(canvas, bounds);
    }

    private void StartMediaLoad(string path)
    {
        int version = ++_loadVersion;
        _ = Task.Run(() => DecodeMediaAsync(path, version));
    }

    private async Task DecodeMediaAsync(string path, int version)
    {
        SKBitmap[]? frames = null;
        long[]? durations = null;
        SKBitmap? staticBitmap = null;
        try
        {
            // Snapshot the file bytes so no file handle outlives this task:
            // decodes read from memory, the source file can be replaced or the
            // tests can delete it without racing an open handle.
            byte[] data = await File.ReadAllBytesAsync(path);

            // Decompression-bomb caps: refuse media whose raw byte size, frame
            // count, or decoded pixel footprint would exhaust memory.
            const int MaxFileBytes = 32 * 1024 * 1024;
            const int MaxFrames = 256;
            const long MaxPixelsPerFrame = 4096L * 4096;
            const long MaxTotalFrameBytes = 512L * 1024 * 1024;

            if (data.Length > MaxFileBytes)
            {
                Context?.LogError($"PictureAndGifWidget: refusing {data.Length} byte media (cap {MaxFileBytes} bytes)");
                return;
            }

            using var dataRef = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(dataRef);
            if (codec != null && codec.FrameCount > 1)
            {
                if (codec.FrameCount > MaxFrames ||
                    (long)codec.Info.Width * codec.Info.Height > MaxPixelsPerFrame ||
                    (long)codec.Info.Width * codec.Info.Height * codec.FrameCount * 4L > MaxTotalFrameBytes)
                {
                    Context?.LogError($"PictureAndGifWidget: refusing {codec.Info.Width}x{codec.Info.Height}x{codec.FrameCount} decode (cap {MaxFrames} frames, {MaxPixelsPerFrame} px/frame, {MaxTotalFrameBytes} total bytes)");
                    return;
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
            }
            else
            {
                // Still image: probe the codec created above; only decode when
                // the frame is within the per-frame pixel cap.
                if (codec == null || (long)codec.Info.Width * codec.Info.Height <= MaxPixelsPerFrame)
                {
                    staticBitmap = SKBitmap.Decode(data);
                }
                else
                {
                    Context?.LogError($"PictureAndGifWidget: refusing {codec.Info.Width}x{codec.Info.Height} still image (cap {MaxPixelsPerFrame} px)");
                }
            }
        }
        catch
        {
            DisposeAll(frames);
            staticBitmap?.Dispose();
            frames = null;
            durations = null;
            staticBitmap = null;
        }

        if (version != _loadVersion || path != _loadedPath)
        {
            DisposeAll(frames);
            staticBitmap?.Dispose();
            return;
        }

        PublishMedia(frames, durations, staticBitmap);
    }

    /// <summary>
    /// Atomically installs decoded media (from the background task) and retires
    /// whatever the render thread might still be drawing.
    /// </summary>
    private void PublishMedia(SKBitmap[]? gifFrames, long[]? durations, SKBitmap? staticBitmap)
    {
        lock (_mediaLock)
        {
            _mediaRetirement.RetireAll(_gifFrames);
            _mediaRetirement.Retire(_staticBitmap);

            _gifFrames = gifFrames;
            _gifFrameDurationsMs = durations;
            _staticBitmap = staticBitmap;
            _gifFrameIndex = 0;
            _gifNextFrameTick = Environment.TickCount64 + FrameDurationMs(durations, 0);
        }

        Context?.RequestRender();
    }

    private void ResetMedia()
    {
        _loadVersion++;
        _loadedPath = "";
        lock (_mediaLock)
        {
            _mediaRetirement.RetireAll(_gifFrames);
            _gifFrames = null;
            _mediaRetirement.Retire(_staticBitmap);
            _staticBitmap = null;
            _gifFrameDurationsMs = null;
            _gifFrameIndex = 0;
        }
        _folderImages = [];
        _folderScanned = false;
        _imageIndex = 0;
        _lastProbePath = "";
        _clipPath?.Dispose();
        _clipPath = null;

        // UI thread (inspector property change / teardown): nothing is mid-draw
        // right now, so dispose retired media promptly instead of waiting for
        // the next render pass.
        _mediaRetirement.DisposeRetired();
    }

    private static void DisposeAll(SKBitmap[]? frames)
    {
        if (frames == null) return;
        foreach (var frame in frames)
        {
            frame?.Dispose();
        }
    }

    private static long FrameDurationMs(long[]? durations, int frameIndex)
        => durations is { Length: > 0 } ? durations[Math.Min(frameIndex, durations.Length - 1)] : 100L;

    // The rounded clip path is cached per (bounds, radius): the media draw
    // clips every frame, but the geometry changes only on a resize or a
    // corner-radius edit.
    private SKPath? _clipPath;
    private SKRect _clipBounds;
    private float _clipRadius = -1f;

    private void DrawImage(SKCanvas canvas, SKRect bounds, SKBitmap bitmap)
    {
        if (bitmap == null) return;

        canvas.Save();
        float radius = Math.Clamp(CornerRadius, 0f, Math.Min(bounds.Width, bounds.Height) / 2f);
        if (_clipPath is null || _clipBounds != bounds
            || BitConverter.SingleToInt32Bits(_clipRadius) != BitConverter.SingleToInt32Bits(radius))
        {
            _clipBounds = bounds;
            _clipRadius = radius;
            _clipPath ??= new SKPath();
#pragma warning disable CS0618 // SKPath.Rewind/AddRoundRect are obsolete in favor of SKPathBuilder, whose Snapshot() allocates a new SKPath per call — the clip path object is reused and rebuilt instead (zero-alloc hot path).
            _clipPath.Rewind();
            _clipPath.AddRoundRect(bounds, radius, radius);
#pragma warning restore CS0618
        }
        canvas.ClipPath(_clipPath);
        canvas.DrawBitmap(bitmap, GetDrawRect(bounds, bitmap.Width, bitmap.Height), new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
    }

    private SKRect GetDrawRect(SKRect bounds, int imgW, int imgH)
    {
        if (FitMode == "Stretch")
        {
            return bounds;
        }

        float scale = FitMode == "Contain"
            ? Math.Min(bounds.Width / imgW, bounds.Height / imgH)
            : Math.Max(bounds.Width / imgW, bounds.Height / imgH);

        float w = imgW * scale;
        float h = imgH * scale;
        return new SKRect(bounds.MidX - w / 2f, bounds.MidY - h / 2f, bounds.MidX + w / 2f, bounds.MidY + h / 2f);
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 36f);
        using var iconPaint = new SKPaint { Color = textColor, IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "🖼️", bounds.MidX, bounds.MidY - 10f, iconFont, iconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);
        using var labelPaint = new SKPaint { Color = textColor, IsAntialias = true };
        // The cycle hint only applies when the source mode actually cycles —
        // a single-image widget must not promise a tap-to-cycle behavior.
        bool cycles = SourceMode == "Folder (Cycle)" || (SourceMode == "Auto" && Directory.Exists(ImagePath));
        string hint = cycles ? "Click/Tap to Cycle Pictures" : "Tap to set an Image Path";
        TextRenderHelper.DrawCenteredText(canvas, hint, bounds.MidX, bounds.MidY + 25f, labelFont, labelPaint);
    }

    private string? GetActiveImageFile()
    {
        bool singleMode = SourceMode == "Single Image";
        bool folderMode = SourceMode == "Folder (Cycle)";

        if (!folderMode && ProbeFileExists(ImagePath))
        {
            return ImagePath;
        }

        if (!singleMode && Directory.Exists(ImagePath))
        {
            if (!_folderScanned)
            {
                _folderImages = Directory.GetFiles(ImagePath, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                _folderScanned = true;
            }

            if (_folderImages.Length > 0)
            {
                _imageIndex %= _folderImages.Length;
                return _folderImages[_imageIndex];
            }
        }

        return null;
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        bool folderMode = SourceMode == "Folder (Cycle)";
        bool autoFolder = SourceMode == "Auto" && Directory.Exists(ImagePath);

        if (eventType == TouchEventType.TouchUp && _folderImages.Length > 0 && (folderMode || autoFolder))
        {
            _imageIndex = (_imageIndex + 1) % _folderImages.Length;
            _loadedPath = "";
            Context?.RequestRender();
        }
    }

    public override ValueTask DisposeAsync()
    {
        ResetMedia();
        _mediaRetirement.DisposeAll();
        return base.DisposeAsync();
    }
}
