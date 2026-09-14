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
[WidgetMetadata("aida_panel", "AIDA64 Panel", Category = "System Monitoring", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class AidaPanelWidget : ModernWidgetBase
{
    private readonly AidaPanelMaster? _master;
    private readonly AidaMmapReader? _reader;
    private SKBitmap? _frameBitmap;
    private long _lastFrameVersion;
    private string? _lastLoggedError;

    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    /// <summary>
    /// Binds the production master: it registers the vendor's AIDA64 widget slot
    /// and drives the publish/ack protocol on a background loop, so AIDA64
    /// publishes live frames. The widget only draws the published copy.
    /// </summary>
    public AidaPanelWidget()
        : this(AidaPanelService.CreateProduction())
    {
    }

    /// <summary>Read-only seam (tests, and the path used when another master owns the slot).</summary>
    internal AidaPanelWidget(AidaMmapReader reader)
    {
        _reader = reader;
    }

    /// <summary>Master seam (tests).</summary>
    internal AidaPanelWidget(AidaPanelMaster master)
    {
        _master = master;
    }

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        if (!TryAcquireFrame(out byte[] buffer, out int pixelOffset, out int length, out int width, out int height, out long version))
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        // Version-token discipline: the master hands out a monotonic generation
        // per published frame; the read-only path hashes the pixels (only a
        // changed frame rebuilds the bitmap).
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
            canvas.DrawBitmap(_frameBitmap, src, dst, new SKSamplingOptions(SKFilterMode.Linear));
        }
    }

    /// <summary>
    /// The frame to draw: the master's published copy when the master is bound
    /// (it starts the background loop lazily), else a direct read of the map
    /// slot (the fallback for when another master owns the slot, and the seam
    /// the tests drive). False means "no frame" — the caller draws the
    /// placeholder.
    /// </summary>
    private bool TryAcquireFrame(out byte[] buffer, out int pixelOffset, out int length, out int width, out int height, out long version)
    {
        if (_master is not null)
        {
            _master.Start();
            var published = _master.TryGetPublished();
            if (published is null)
            {
                buffer = [];
                pixelOffset = 0;
                length = 0;
                width = 0;
                height = 0;
                version = 0;
                return false;
            }

            buffer = published.Pixels;
            pixelOffset = 0;
            length = published.Length;
            width = published.Width;
            height = published.Height;
            version = published.Generation;
            return true;
        }

        var frame = _reader!.TryReadFrame();
        if (frame is null)
        {
            LogReadFailureOnce();
            buffer = [];
            pixelOffset = 0;
            length = 0;
            width = 0;
            height = 0;
            version = 0;
            return false;
        }

        _lastLoggedError = null;
        buffer = frame.Buffer;
        pixelOffset = frame.PixelOffset;
        length = frame.PayloadLength;
        width = frame.Width;
        height = frame.Height;
        version = ComputeBufferVersion(buffer, pixelOffset, length, width, height);
        return true;
    }

    private void LogReadFailureOnce()
    {
        if (_reader is null || string.Equals(_reader.LastError, _lastLoggedError, StringComparison.Ordinal))
            return;
        _lastLoggedError = _reader.LastError;
        Context?.LogError($"AIDA64 panel unavailable: {_reader.LastError}");
    }

    private static long ComputeBufferVersion(byte[] buffer, int offset, int length, int width, int height)
    {
        // FNV-1a over the WHOLE pixel payload, seeded with the geometry. The
        // former sampled XOR folded 1.2 MB into 8 bits (it ignored the green
        // channel and every unsampled byte, and collided about 1/256), so a
        // redraw could leave the previous panel on screen. The full hash is a
        // few hundred microseconds and only matters when a frame arrives.
        const uint fnvOffset = 2166136261u;
        const uint fnvPrime = 16777619u;
        uint hash = fnvOffset;
        hash = (hash ^ (uint)width) * fnvPrime;
        hash = (hash ^ (uint)height) * fnvPrime;
        for (int i = offset; i < offset + length; i++)
        {
            hash = (hash ^ buffer[i]) * fnvPrime;
        }

        return hash;
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
        _master?.Dispose();
        _reader?.Dispose();
        RecycleBitmap();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
        return base.DisposeAsync();
    }
}
