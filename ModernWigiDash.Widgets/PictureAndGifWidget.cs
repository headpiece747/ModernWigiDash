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
    private SKCodec? _gifCodec;
    private SKBitmap[]? _gifFrames;
    private int _gifFrameIndex;
    private long _gifNextFrameTick;
    private string _loadedPath = "";

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
        string? currentFile = GetActiveImageFile();

        if (!string.IsNullOrEmpty(currentFile) && File.Exists(currentFile))
        {
            if (currentFile != _loadedPath)
            {
                LoadMedia(currentFile);
            }

            if (_gifFrames is { Length: > 1 })
            {
                if (Environment.TickCount64 >= _gifNextFrameTick)
                {
                    _gifFrameIndex = (_gifFrameIndex + 1) % _gifFrames.Length;
                    _gifNextFrameTick = Environment.TickCount64 + GifFrameDurationMs(_gifFrameIndex);
                }
                DrawImage(canvas, bounds, _gifFrames[_gifFrameIndex]);
                return;
            }

            if (_staticBitmap != null)
            {
                DrawImage(canvas, bounds, _staticBitmap);
                return;
            }
        }

        DrawPlaceholder(canvas, bounds);
    }

    private void LoadMedia(string path)
    {
        ResetMedia();
        _loadedPath = path;

        try
        {
            _gifCodec = SKCodec.Create(path);
            if (_gifCodec != null && _gifCodec.FrameCount > 1)
            {
                int frameCount = _gifCodec.FrameCount;
                SKImageInfo info = _gifCodec.Info;
                _gifFrames = new SKBitmap[frameCount];

                for (int i = 0; i < frameCount; i++)
                {
                    var frame = new SKBitmap(info);
                    IntPtr pixels = frame.GetPixels();
                    var options = new SKCodecOptions { FrameIndex = i };
                    if (_gifCodec.GetPixels(info, pixels, options) != SKCodecResult.Success)
                    {
                        frame.Dispose();
                        frame = new SKBitmap(info);
                    }
                    _gifFrames[i] = frame;
                }

                _gifFrameIndex = 0;
                _gifNextFrameTick = Environment.TickCount64 + GifFrameDurationMs(0);
                return;
            }

            _gifCodec?.Dispose();
            _gifCodec = null;
            _staticBitmap = SKBitmap.Decode(path);
        }
        catch
        {
            ResetMedia();
        }
    }

    private void ResetMedia()
    {
        _loadedPath = "";
        if (_gifFrames != null)
        {
            foreach (var frame in _gifFrames)
            {
                frame.Dispose();
            }
            _gifFrames = null;
        }
        _gifCodec?.Dispose();
        _gifCodec = null;
        _staticBitmap?.Dispose();
        _staticBitmap = null;
        _gifFrameIndex = 0;
        _folderImages = [];
        _imageIndex = 0;
    }

    private long GifFrameDurationMs(int frameIndex)
    {
        if (_gifCodec != null && frameIndex >= 0 && frameIndex < _gifCodec.FrameInfo.Length)
        {
            long ms = _gifCodec.FrameInfo[frameIndex].Duration;
            if (ms > 0) return ms;
        }
        return 100L;
    }

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
        using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, 36f);
        using var iconPaint = new SKPaint { Color = textColor, IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "🖼️", bounds.MidX, bounds.MidY - 10f, iconFont, iconPaint);

        using var labelFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, 12f);
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
