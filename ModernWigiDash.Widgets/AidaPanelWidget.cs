using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The AIDA64 image-frame widget (ADR-0022): blits rendered AIDA64 sensor-panel
/// bytes from the vendor WigiDash service's IAidaPanel facet. NOT a value feed:
/// the widget draws the pixel buffer directly (the Picture-widget decode/blit/
/// version-token discipline applies). Pixel layout (expected 1016×592×2 = RGB565)
/// is probed live during build. Degrades to the house placeholder when the vendor
/// service is absent (ADR-0017 image).
/// </summary>
[WidgetMetadata("aida_panel", "AIDA64 Panel", Category = "System Monitoring")]
public sealed class AidaPanelWidget : ModernWidgetBase
{
    /// <summary>The "Offset": byte offset into the AIDA64 mmap to read from.</summary>
    [WidgetProperty("Offset", WidgetPropertyType.Number, "Byte offset into the AIDA64 mmap", 0f)]
    public float Offset { get; set; } = 0f;

    /// <summary>The "Length": number of bytes to read per frame.</summary>
    [WidgetProperty("Length", WidgetPropertyType.Number, "Bytes to read per frame (default: full framebuffer)", 1208384f)]
    public float Length { get; set; } = 1208384f; // 1016 * 592 * 2

    private SKBitmap? _frameBitmap;
    private long _lastFrameVersion;

    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var client = VendorService.Instance;
        if (client == null || !client.Aida.IsReady)
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        var offset = Math.Max(0, (int)Offset);
        var length = Math.Max(0, (int)Length);
        if (length <= 0)
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        var buffer = client.Aida.ReadAidaMmap(offset, length);
        if (buffer == null || buffer.Length < length)
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        // Version-token discipline: only rebuild the bitmap when the buffer
        // content changes (a new frame was read).
        var version = ComputeBufferVersion(buffer);
        if (version != _lastFrameVersion || _frameBitmap == null)
        {
            RecycleBitmap();
            _frameBitmap = BuildBitmap(buffer, length);
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
            canvas.DrawBitmap(_frameBitmap, src, dst, null);
        }
    }

    private static long ComputeBufferVersion(byte[] buffer)
    {
        long hash = 0;
        for (int i = 0; i < buffer.Length; i += 4)
            hash ^= buffer[i];
        return hash;
    }

    private static SKBitmap? BuildBitmap(byte[] buffer, int length)
    {
        const int FrameWidth = 1016;
        const int FrameHeight = 592;
        var expectedBytes = FrameWidth * FrameHeight * 2;
        if (length < expectedBytes)
            return null;

        // Convert RGB565 little-endian to RGBA via per-pixel expansion.
        var rgba = new byte[FrameWidth * FrameHeight * 4];
        for (int y = 0; y < FrameHeight; y++)
        {
            var rowSrc = y * (expectedBytes / FrameHeight);
            var rowDst = y * FrameWidth * 4;
            for (int x = 0; x < FrameWidth; x++)
            {
                ushort rgb565 = (ushort)(buffer[rowSrc + x * 2] | (buffer[rowSrc + x * 2 + 1] << 8));
                int r5 = (rgb565 >> 11) & 0x1F;
                int g6 = (rgb565 >> 5) & 0x3F;
                int b5 = rgb565 & 0x1F;
                rgba[rowDst + x * 4] = (byte)((r5 << 3) | (r5 >> 2));
                rgba[rowDst + x * 4 + 1] = (byte)((g6 << 2) | (g6 >> 4));
                rgba[rowDst + x * 4 + 2] = (byte)((b5 << 3) | (b5 >> 2));
                rgba[rowDst + x * 4 + 3] = 255;
            }
        }

        var info = new SKImageInfo(FrameWidth, FrameHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        for (int y = 0; y < FrameHeight; y++)
        {
            for (int x = 0; x < FrameWidth; x++)
            {
                var idx = (y * FrameWidth + x) * 4;
                var color = new SKColor(rgba[idx], rgba[idx + 1], rgba[idx + 2], rgba[idx + 3]);
                bitmap.SetPixel(x, y, color);
            }
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
            "Vendor service not connected",
            text, _placeholderTitlePaint, _placeholderSubPaint);
    }

    public override ValueTask DisposeAsync()
    {
        RecycleBitmap();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
        return base.DisposeAsync();
    }
}
