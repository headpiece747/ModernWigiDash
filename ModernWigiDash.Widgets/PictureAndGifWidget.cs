namespace ModernWigiDash.Widgets;

/// <summary>
/// Image and animated-GIF viewer: draws a single file or cycles a folder of
/// images, decoded by the MediaDecoder module on a background thread. The
/// source-resolution table (file vs. folder, the tap-to-cycle promise) lives
/// in PictureSourcePolicy.
/// </summary>
[WidgetMetadata("picture_viewer", "Picture & GIF Viewer", Category = "Social & Visual")]
public class PictureAndGifWidget : ModernWidgetBase
{
    /// <summary>The "Image Folder/File Path" property: path to the image or folder of images.</summary>
    [WidgetProperty("Image Folder/File Path", WidgetPropertyType.Path, "Path to image or folder of images", "C:\\Pictures")]
    public string ImagePath { get; set; } = "C:\\Pictures";

    /// <summary>The "Source Mode" property: auto-detects file or folder, or forces one mode.</summary>
    [WidgetProperty("Source Mode", WidgetPropertyType.Choice, "Auto detects file or folder; forces one mode when set", "Auto", "Auto", "Single Image", "Folder (Cycle)")]
    public string SourceMode { get; set; } = "Auto";

    /// <summary>The "Fit Mode" property: aspect ratio scaling mode (Cover, Contain, Stretch).</summary>
    [WidgetProperty("Fit Mode", WidgetPropertyType.Choice, "Aspect ratio scaling mode", "Cover", "Cover", "Contain", "Stretch")]
    public string FitMode { get; set; } = "Cover";

    /// <summary>The "Corner Radius" property: the rounded corners radius.</summary>
    [WidgetProperty("Corner Radius", WidgetPropertyType.Number, "Rounded corners radius", 16f)]
    public float CornerRadius { get; set; } = 16f;

    /// <summary>The "Text Color" property: placeholder icon and hint color.</summary>
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
    private bool _disposed;

    /// <summary>Test seam: clock for the folder-rescan throttle (defaults to the system clock).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Last folder scan time — files added to a cycling folder while
    /// the app runs appear on the next throttled rescan, not only after a
    /// property change.</summary>
    private DateTimeOffset _folderLastScan = DateTimeOffset.MinValue;

    /// <summary>How often a cycling folder is rescanned for new files.</summary>
    private static readonly TimeSpan FolderRescanPeriod = TimeSpan.FromSeconds(30);

    /// <summary>The currently listed folder images (test seam).</summary>
    internal string[] _folderImagesForTest => _folderImages;

    // Last-probed-path existence cache: File.Exists per frame is a filesystem
    // hit; the probe is re-done only when the path changes (ResetMedia clears
    // the key on ImagePath/SourceMode changes).
    private string _lastProbePath = "";
    private bool _lastProbeExists;

    // The folder-existence probe follows the same rule (Directory.Exists is a
    // syscall per frame; the cached verdict re-probes only when the path
    // changes). The folder's file list already rescans on its own 30 s
    // throttle, so a folder added or removed mid-run is picked up on the
    // next rescan (or on a path change) without a per-frame disk hit.
    private string _lastDirProbePath = "";
    private bool _lastDirProbeExists;

    private bool ProbeDirExists(string path)
    {
        if (!string.Equals(path, _lastDirProbePath, StringComparison.Ordinal))
        {
            _lastDirProbePath = path;
            _lastDirProbeExists = Directory.Exists(path);
        }
        return _lastDirProbeExists;
    }

    // Hoisted placeholder paints (the 30 FPS render allocates no SKPaint).
    private readonly SKPaint _placeholderIconPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderLabelPaint = new() { IsAntialias = true };

    private bool ProbeFileExists(string path)
    {
        if (!string.Equals(path, _lastProbePath, StringComparison.Ordinal))
        {
            _lastProbePath = path;
            _lastProbeExists = File.Exists(path);
        }
        return _lastProbeExists;
    }

    // Media is decoded on a background thread by the MediaDecoder module (bytes
    // in, installable MediaDecodeResult out, never throws for malformed
    // media) and published atomically here. The render thread may be drawing
    // a bitmap at publish time, so replaced bitmaps are retired via
    // _mediaRetirement and disposed only on the UI render thread (start of
    // Render) or widget teardown — never from the decode task. _mediaLock
    // guards the field swaps below (frames array, frame index, static
    // bitmap); the retirement list itself lives inside RetiredBitmapSet.
    private readonly Lock _mediaLock = new();
    private readonly RetiredBitmapSet _mediaRetirement = new();

    // Test seams: the most recently started decode task (tests await it to
    // make the async decode deterministic) and the installed media state
    // under the media lock (null when nothing is installed - the placeholder
    // state).
    private Task? _pendingDecodeTask;
    internal Task? PendingDecodeTaskForTest => _pendingDecodeTask;

    internal (SKBitmap[]? Frames, SKBitmap? Still) InstalledMediaForTest
    {
        get
        {
            lock (_mediaLock)
            {
                return (_gifFrames, _staticBitmap);
            }
        }
    }

    // The current GIF frame index under the media lock, so the Clock-driven
    // advance is assertable without pixel sampling.
    internal int GifFrameIndexForTest
    {
        get
        {
            lock (_mediaLock)
            {
                return _gifFrameIndex;
            }
        }
    }

    /// <summary>Resets the installed media when the path or the source mode changes.</summary>
    /// <param name="propertyName">The property that changed.</param>
    /// <param name="newValue">The property's new value.</param>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (propertyName is nameof(ImagePath) or nameof(SourceMode))
        {
            ResetMedia();
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    /// <summary>
    /// Draws the active image or GIF frame (retiring replaced bitmaps on this
    /// thread), or the placeholder when nothing is installed.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _mediaRetirement.DisposeRetired();

        string? currentFile = GetActiveImageFile();

        if (!string.IsNullOrEmpty(currentFile) && ProbeFileExists(currentFile))
        {
            if (!string.Equals(currentFile, _loadedPath, StringComparison.Ordinal))
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

                if (frames is { Length: > 1 } && Clock.GetTimestamp() >= _gifNextFrameTick)
                {
                    frameIndex = (_gifFrameIndex + 1) % frames.Length;
                    _gifFrameIndex = frameIndex;
                    _gifNextFrameTick = Clock.GetTimestamp() + TimeSpan.FromMilliseconds(FrameDurationMs(_gifFrameDurationsMs, frameIndex)).Ticks;
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
        _pendingDecodeTask = Task.Run(() => DecodeMediaAsync(path, version));
    }

    private async Task DecodeMediaAsync(string path, int version)
    {
        byte[] data;
        try
        {
            // Snapshot the file bytes so no file handle outlives this task:
            // decodes read from memory, the source file can be replaced or
            // the tests can delete it without racing an open handle.
            data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch
        {
            // The file vanished mid-read: nothing to install, the render
            // tick re-probes and re-loads.
            return;
        }

        // The decode itself (bomb caps, delta compositing, the malformed
        // media rule) is the MediaDecoder module: it never throws and hands
        // back an installable result or None with its buffers disposed.
        MediaDecodeResult decoded = MediaDecoder.Decode(data, msg => Context?.LogError($"PictureAndGifWidget: {msg}"));

        if (version != _loadVersion || !string.Equals(path, _loadedPath, StringComparison.Ordinal))
        {
            decoded.DisposeMedia();
            return;
        }

        PublishMedia(decoded.Frames, decoded.Durations, decoded.Still);
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
            _gifNextFrameTick = Clock.GetTimestamp() + TimeSpan.FromMilliseconds(FrameDurationMs(durations, 0)).Ticks;
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
        _lastDirProbePath = "";
        _clipPath?.Dispose();
        _clipPath = null;

        // UI thread (inspector property change / teardown): nothing is mid-draw
        // right now, so dispose retired media promptly instead of waiting for
        // the next render pass.
        _mediaRetirement.DisposeRetired();
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
        if (string.Equals(FitMode, "Stretch", StringComparison.Ordinal))
        {
            return bounds;
        }

        float scale = string.Equals(FitMode, "Contain", StringComparison.Ordinal)
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
        _placeholderIconPaint.Color = textColor;
        TextRenderHelper.DrawCenteredText(canvas, "🖼️", bounds.MidX, bounds.MidY - 10f, iconFont, _placeholderIconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);
        _placeholderLabelPaint.Color = textColor;
        // The cycle hint only applies when the source actually cycles (the
        // policy verdict) — a single-image widget or a missing folder must not
        // promise a tap-to-cycle behavior it cannot keep.
        string hint = PictureSourcePolicy.PlaceholderHint(
            PictureSourcePolicy.CanCycle(SourceMode, ProbeFileExists(ImagePath), ProbeDirExists(ImagePath)));
        TextRenderHelper.DrawCenteredText(canvas, hint, bounds.MidX, bounds.MidY + 25f, labelFont, _placeholderLabelPaint);
    }

    private string? GetActiveImageFile()
    {
        switch (PictureSourcePolicy.Resolve(SourceMode, ProbeFileExists(ImagePath), ProbeDirExists(ImagePath)))
        {
            case PictureSourcePolicy.PictureSourceKind.File:
                return ImagePath;

            case PictureSourcePolicy.PictureSourceKind.Folder:
                // Rescan on first use and then on a throttled cadence: files
                // added to a cycling folder while the app runs appear within
                // one period, without a per-frame disk scan. A folder removed
                // (or made unreadable) mid-run degrades to "no images" instead
                // of throwing into the render tick — the next rescan or path
                // change picks the folder back up.
                if (!_folderScanned || Clock.GetUtcNow() - _folderLastScan >= FolderRescanPeriod)
                {
                    try
                    {
                        _folderImages = Directory.GetFiles(ImagePath, "*.*")
                            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    }
                    catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
                    {
                        _folderImages = [];
                    }
                    _folderScanned = true;
                    _folderLastScan = Clock.GetUtcNow();
                    // The rescan is the folder's existence verdict too: a
                    // folder deleted mid-run must stop promising a
                    // tap-to-cycle within one rescan period (the CanCycle
                    // promise reads the probe), not only on the next path
                    // edit. Invalidating the probe makes the next
                    // ProbeDirExists re-stat, then re-caches for the
                    // following period.
                    _lastDirProbePath = "";
                }

                if (_folderImages.Length > 0)
                {
                    _imageIndex %= _folderImages.Length;
                    return _folderImages[_imageIndex];
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>A release over an actually cycling folder advances to the next image and requests a render.</summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local space.</param>
    /// <param name="eventType">The touch event type.</param>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        // The touch gate is the policy's cycle verdict: only an actually
        // cycling folder advances on tap (the scanned-list guard stays — a
        // folder with no images yet has nothing to cycle).
        if (eventType == TouchEventType.TouchUp && _folderImages.Length > 0 &&
            PictureSourcePolicy.CanCycle(SourceMode, ProbeFileExists(ImagePath), ProbeDirExists(ImagePath)))
        {
            _imageIndex = (_imageIndex + 1) % _folderImages.Length;
            _loadedPath = "";
            Context?.RequestRender();
        }
    }

    /// <summary>Resets the media, disposes the placeholder paints and the retired bitmaps, and releases the widget's Skia surfaces.</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        ResetMedia();
        _placeholderIconPaint.Dispose();
        _placeholderLabelPaint.Dispose();
        _mediaRetirement.DisposeAll();
        return base.DisposeAsync();
    }
}
