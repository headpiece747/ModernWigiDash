using ModernWigiDash.Hardware.Aida64;

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
    private readonly bool _masterBorrowed;
    private readonly AidaMmapReader? _reader;
    private string? _lastLoggedError;

    // The draw owner: frame bitmap + version-token discipline, the
    // placeholder paints, and the bitmap build (the widget's draw paths and
    // their disposal live in one place).
    private readonly AidaPanelCanvas _canvas = new();

    /// <summary>
    /// Borrows the process-wide master: it registers the vendor's AIDA64 widget
    /// slot and drives the publish/ack protocol on a background loop, so AIDA64
    /// publishes live frames. The widget only draws the published copy. The
    /// master is SHARED (slot 0 is one slot), so two AIDA64 widgets never fight
    /// over it.
    /// </summary>
    public AidaPanelWidget()
    {
        _master = AidaPanelService.Acquire();
        _masterBorrowed = true;
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
            _canvas.RenderPlaceholder(canvas, bounds);
            return;
        }

        _canvas.RenderFrame(canvas, bounds, buffer, pixelOffset, length, width, height, version);
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
        version = frame.Version;
        return true;
    }

    private void LogReadFailureOnce()
    {
        if (_reader is null || string.Equals(_reader.LastError, _lastLoggedError, StringComparison.Ordinal))
            return;
        _lastLoggedError = _reader.LastError;
        Context?.LogError($"AIDA64 panel unavailable: {_reader.LastError}");
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        if (_masterBorrowed)
        {
            AidaPanelService.Release();
        }
        else
        {
            _master?.Dispose();
        }

        _reader?.Dispose();
        _canvas.Dispose();
        return base.DisposeAsync();
    }
}
