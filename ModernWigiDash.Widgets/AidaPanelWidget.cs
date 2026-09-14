using System.Runtime.InteropServices;
using ModernWigiDash.Hardware.Aida64;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The AIDA64 image-frame widget: blits rendered AIDA64 sensor-panel bytes read
/// directly from the vendor's named shared-memory map (Global\Gskill_Frontier_Aida64Widget).
/// NOT a value feed: the widget draws the pixel buffer directly (the Picture-widget
/// decode/blit/version-token discipline applies). Pixel layout (expected 1016x592x2 =
/// RGB565) is validated against the map's own header during each read. Degrades to the
/// house placeholder when AIDA64 is not running (ADR-0017 image).
/// </summary>
[WidgetMetadata("aida_panel", "AIDA64 Panel", Category = "System Monitoring")]
public sealed class AidaPanelWidget : ModernWidgetBase
{
    private readonly AidaMmapReader _reader;
    private SKBitmap? _frameBitmap;
    private long _lastFrameVersion;
    private string? _lastLoggedError;

    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    /// <summary>Binds the production reader over the vendor's named shared map.</summary>
    public AidaPanelWidget()
        : this(new AidaMmapReader(new MemoryMappedAidaMmapSource()))
    {
    }

    internal AidaPanelWidget(AidaMmapReader reader)
    {
        _reader = reader;
    }

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var frame = _reader.TryReadFrame();
        if (frame == null)
        {
            LogReadFailureOnce();
            DrawPlaceholder(canvas, bounds);
            return;
        }

        _lastLoggedError = null;

        // Version-token discipline: only rebuild the bitmap when the buffer
        // content changes (a new frame was read).
        var version = ComputeBufferVersion(frame.Payload, frame.PayloadLength);
        if (version != _lastFrameVersion || _frameBitmap == null)
        {
            RecycleBitmap();
            _frameBitmap = BuildBitmap(frame.Payload, frame.PayloadLength, frame.Width, frame.Height);
            _lastFrameVersion = version;
        }

        if (_frameBitmap != null && _frameBitmap.Width > 0 && _frameBitmap.Height > 0)
        {
            var scale = Math.Min(bounds.Width / _frameBitmap.Width, bounds.Height / _frameBitmap.Height);
            var drawWidth = _frameBitmap.Width * scale;
            var drawHeight = _frameBitmap.Height * scale;
            var offsetX = bounds.Left + (bounds.Width - drawWidth) / 2f;
            var offsetY = bounds.Top + (bounds.Height - drawHeight) / 2f;
            var src = new SKRect(0, 0, _frameBitmap.Width, _frameBitmap.Height);
            var dst = new SKRect(offsetX, offsetY, offsetX + drawWidth, offsetY + drawHeight);
            canvas.DrawBitmap(_frameBitmap, src, dst, new SKSamplingOptions(SKFilterMode.Linear));
        }
    }

    private void LogReadFailureOnce()
    {
        if (string.Equals(_reader.LastError, _lastLoggedError, StringComparison.Ordinal))
            return;
        _lastLoggedError = _reader.LastError;
        Context?.LogError($"AIDA64 panel unavailable: {_reader.LastError}");
    }

    private static long ComputeBufferVersion(byte[] buffer, int length)
    {
        long hash = 0;
        for (int i = 0; i < length; i += 4)
            hash ^= buffer[i];
        return hash;
    }

    internal static SKBitmap? BuildBitmap(byte[] payload, int payloadLength, int width, int height)
    {
        if (width <= 0 || height <= 0 || payloadLength != width * height * 2)
            return null;

        // The payload is an RGB565 little-endian BMP body, stored bottom-up
        // (the reader validates a positive biHeight), while an SKBitmap is
        // top-down. Copy rows in reverse so the panel is not drawn upside
        // down. A straight copy into an Rgb565 bitmap still avoids a per-pixel
        // expansion (the old RGBA path allocated a 4x buffer per rebuild).
        var info = new SKImageInfo(width, height, SKColorType.Rgb565, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        var pixels = bitmap.GetPixels();
        int srcStride = ((width * 2) + 3) / 4 * 4;
        int copyBytes = width * 2;
        for (int row = 0; row < height; row++)
        {
            int srcOffset = (height - 1 - row) * srcStride;
            Marshal.Copy(payload, srcOffset, IntPtr.Add(pixels, row * bitmap.RowBytes), copyBytes);
        }

        return bitmap;
    }

    private void RecycleBitmap()
    {
        _frameBitmap?.Dispose();
        _frameBitmap = null;
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        var text = ColorOf("#9CA3AF", SKColors.Gray);
        TextRenderHelper.DrawTitleSubtitlePlaceholder(
            canvas, bounds,
            "AIDA64",
            "AIDA64 not connected",
            text, _placeholderTitlePaint, _placeholderSubPaint);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        _reader.Dispose();
        RecycleBitmap();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
        return base.DisposeAsync();
    }
}
