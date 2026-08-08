using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("picture_viewer", "Picture & GIF Viewer", Description = "Displays pictures and animated GIFs with rounded borders and click-to-cycle folder viewing.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size2x2)]
public class PictureAndGifWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
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
    private int _imageIndex = 0;
    private SKBitmap? _staticBitmap;
    private SKBitmap[]? _gifFrames;
    private long[]? _gifFrameDurationsMs;
    private int _gifFrameIndex;
    private long _gifNextFrameTick;
    private string _loadedPath = "";
    private int _loadVersion;

    // Media is decoded on a background thread and published atomically. The
    // render thread may be drawing a bitmap at publish time, so replaced
    // bitmaps are retired and disposed only on the UI render thread (start of
    // Render) or widget teardown — never from the decode task (same
    // use-after-free class as NowPlayingWidget's album art).
    private readonly Lock _mediaLock = new();
    private readonly List<SKBitmap> _retiredMedia = [];

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
        DisposeRetiredMedia();

        string? currentFile = GetActiveImageFile();

        if (!string.IsNullOrEmpty(currentFile) && File.Exists(currentFile))
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
            byte[] data = File.ReadAllBytes(path);
            using var dataRef = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(dataRef);
            if (codec != null && codec.FrameCount > 1)
            {
                int frameCount = codec.FrameCount;
                SKImageInfo info = codec.Info;
                frames = new SKBitmap[frameCount];
                durations = new long[frameCount];

                for (int i = 0; i < frameCount; i++)
                {
                    var frame = new SKBitmap(info);
                    IntPtr pixels = frame.GetPixels();

                    // GIF frames are deltas: the codec needs the previous
                    // frame's pixels already in the destination to composite
                    // correctly. Without it, delta frames decode as mostly
                    // empty/black regions and the animation flashes black.
                    var options = new SKCodecOptions { FrameIndex = i, PriorFrame = i > 0 ? i - 1 : -1 };
                    if (i > 0)
                    {
                        frames[i - 1].CopyTo(frame);
                    }

                    if (codec.GetPixels(info, pixels, options) != SKCodecResult.Success)
                    {
                        frame.Dispose();
                        frame = new SKBitmap(info);
                    }
                    frames[i] = frame;
                    durations[i] = codec.FrameInfo[i].Duration > 0 ? codec.FrameInfo[i].Duration : 100L;
                }
            }
            else
            {
                staticBitmap = SKBitmap.Decode(data);
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

        PublishMedia(path, frames, durations, staticBitmap);
    }

    /// <summary>
    /// Atomically installs decoded media (from the background task) and retires
    /// whatever the render thread might still be drawing.
    /// </summary>
    private void PublishMedia(string path, SKBitmap[]? gifFrames, long[]? durations, SKBitmap? staticBitmap)
    {
        lock (_mediaLock)
        {
            if (_gifFrames != null)
            {
                foreach (var frame in _gifFrames)
                {
                    _retiredMedia.Add(frame);
                }
            }
            if (_staticBitmap != null)
            {
                _retiredMedia.Add(_staticBitmap);
            }

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
            if (_gifFrames != null)
            {
                foreach (var frame in _gifFrames)
                {
                    _retiredMedia.Add(frame);
                }
                _gifFrames = null;
            }
            if (_staticBitmap != null)
            {
                _retiredMedia.Add(_staticBitmap);
                _staticBitmap = null;
            }
            _gifFrameDurationsMs = null;
            _gifFrameIndex = 0;
        }
        _folderImages = [];
        _imageIndex = 0;

        // UI thread (inspector property change / teardown): nothing is mid-draw
        // right now, so dispose retired media promptly instead of waiting for
        // the next render pass.
        DisposeRetiredMedia();
    }

    /// <summary>
    /// Disposes retired bitmaps. Called only on the UI render thread (start of
    /// <see cref="Render"/>) or on widget teardown — never from the background
    /// decode task — so a bitmap is never freed while the canvas could still be
    /// drawing it.
    /// </summary>
    private void DisposeRetiredMedia()
    {
        lock (_mediaLock)
        {
            if (_retiredMedia.Count == 0) return;
            foreach (var retired in _retiredMedia)
            {
                retired.Dispose();
            }
            _retiredMedia.Clear();
        }
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

    private void DrawImage(SKCanvas canvas, SKRect bounds, SKBitmap bitmap)
    {
        if (bitmap == null) return;

        canvas.Save();
        float radius = Math.Clamp(CornerRadius, 0f, Math.Min(bounds.Width, bounds.Height) / 2f);
        using (var clipBuilder = new SKPathBuilder())
        {
            clipBuilder.AddRoundRect(bounds, radius, radius);
            using var clipPath = clipBuilder.Snapshot();
            canvas.ClipPath(clipPath);
            canvas.DrawBitmap(bitmap, GetDrawRect(bounds, bitmap.Width, bitmap.Height), new SKSamplingOptions(SKFilterMode.Linear));
        }
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
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 36f);
        using var iconPaint = new SKPaint { Color = textColor, IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "🖼️", bounds.MidX, bounds.MidY - 10f, iconFont, iconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);
        using var labelPaint = new SKPaint { Color = textColor, IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "Click/Tap to Cycle Pictures", bounds.MidX, bounds.MidY + 25f, labelFont, labelPaint);
    }

    private string? GetActiveImageFile()
    {
        bool singleMode = SourceMode == "Single Image";
        bool folderMode = SourceMode == "Folder (Cycle)";

        if (!folderMode && File.Exists(ImagePath))
        {
            return ImagePath;
        }

        if (!singleMode && Directory.Exists(ImagePath))
        {
            if (_folderImages.Length == 0)
            {
                _folderImages = Directory.GetFiles(ImagePath, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
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
        return base.DisposeAsync();
    }
}
