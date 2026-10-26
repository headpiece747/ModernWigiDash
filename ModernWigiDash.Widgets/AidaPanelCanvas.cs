using System.Runtime.InteropServices;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The AIDA64 panel widget's draw owner: the frame bitmap with its
/// version-token discipline, the hoisted placeholder paints, and the
/// bitmap build, so the widget's draw paths and their disposal live in
/// one place (the one-draw-owner rule, CONTEXT.md).
/// </summary>
internal sealed class AidaPanelCanvas : IDisposable
{
    private SKBitmap? _frameBitmap;
    private long _lastFrameVersion;

    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    private static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear);

    /// <summary>
    /// Draws the acquired frame letterboxed into the bounds. The
    /// version-token discipline rebuilds the bitmap only when the
    /// generation turns: the master hands out a monotonic generation per
    /// published frame, and the read-only path hashes the pixels (only a
    /// changed frame rebuilds the bitmap).
    /// </summary>
    public void RenderFrame(SKCanvas canvas, SKRect bounds, byte[] buffer, int pixelOffset, int length, int width, int height, long version)
    {
        if (version != _lastFrameVersion || _frameBitmap == null)
        {
            RecycleBitmap();
            _frameBitmap = BuildBitmap(buffer, pixelOffset, length, width, height);
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
            canvas.DrawBitmap(_frameBitmap, src, dst, LinearSampling);
        }
    }

    /// <summary>The house placeholder (ADR-0017 image) drawn when no frame is available.</summary>
    public void RenderPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        var text = SkColorCache.ColorOf("#9CA3AF", SKColors.Gray);
        TextRenderHelper.DrawTitleSubtitlePlaceholder(
            canvas, bounds,
            "AIDA64",
            "AIDA64 not connected",
            text, _placeholderTitlePaint, _placeholderSubPaint);
    }

    private void RecycleBitmap()
    {
        _frameBitmap?.Dispose();
        _frameBitmap = null;
    }

    internal static SKBitmap? BuildBitmap(byte[] buffer, int pixelOffset, int payloadLength, int width, int height)
    {
        if (width <= 0 || height <= 0 || payloadLength != width * height * 2)
            return null;

        // The payload is an RGB565 little-endian BMP body, stored bottom-up
        // (the reader validates a positive biHeight), while an SKBitmap is
        // top-down. Copy rows in reverse so the panel is not drawn upside
        // down. The stride is TIGHT (width*2): the reader validates a tightly
        // packed payload, so a 4-byte-padded stride would read past the frame
        // the reader validated and shift every row (a silent misrender).
        var info = new SKImageInfo(width, height, SKColorType.Rgb565, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        var pixels = bitmap.GetPixels();
        int srcStride = width * 2;
        int copyBytes = width * 2;
        for (int row = 0; row < height; row++)
        {
            int srcOffset = pixelOffset + ((height - 1 - row) * srcStride);
            if (srcOffset < pixelOffset || srcOffset + copyBytes > buffer.Length)
            {
                bitmap.Dispose();
                return null;
            }

            Marshal.Copy(buffer, srcOffset, IntPtr.Add(pixels, row * bitmap.RowBytes), copyBytes);
        }

        return bitmap;
    }

    /// <summary>Disposes the frame bitmap and the placeholder paints.</summary>
    public void Dispose()
    {
        RecycleBitmap();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
    }
}
