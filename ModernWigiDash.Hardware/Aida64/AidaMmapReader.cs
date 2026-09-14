namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The AIDA64 panel-frame reader (Hardware): validates the vendor shared map's
/// header and the embedded BMP header, then exposes one RGB565 frame snapshot
/// per call. Pure policy over the <see cref="IAidaMmapSource"/> seam, so the
/// validation rules are drivable without the real map.
/// </summary>
public sealed class AidaMmapReader : IDisposable
{
    internal const int MapHeaderSize = 24 + 92;
    internal const int MagicTagOffset = 8;
    internal const int NumWidgetsOffset = 16;
    internal const int WidgetWidthOffset = 40;
    internal const int WidgetHeightOffset = 44;
    internal const int BitmapFormatOffset = 88;
    internal const int BitmapSizeOffset = 92;
    internal const int BitmapOffsetOffset = 96;
    internal const int BitmapFormatRgb565 = 135174;

    internal const int BmpHeaderSize = 66;
    internal const int BmpPixelOffset = 66;
    internal const int BmpBitCount = 16;
    internal const int BmpCompression = 3;
    internal const uint Rgb565RedMask = 0xF800;
    internal const uint Rgb565GreenMask = 0x07E0;
    internal const uint Rgb565BlueMask = 0x001F;

    internal const int PanelWidth = 1016;
    internal const int PanelHeight = 592;
    internal const int PanelPayloadBytes = PanelWidth * PanelHeight * 2;

    private readonly IAidaMmapSource _mapSource;
    private readonly byte[] _headerBuffer = new byte[MapHeaderSize];
    private readonly byte[] _bmpHeaderBuffer = new byte[BmpHeaderSize];
    private readonly byte[] _payloadBuffer = new byte[PanelPayloadBytes];

    /// <summary>
    /// Binds the reader to the map source it reads from.
    /// </summary>
    public AidaMmapReader(IAidaMmapSource mapSource)
    {
        _mapSource = mapSource;
    }

    /// <summary>
    /// The reason the last <see cref="TryReadFrame"/> returned null, or null
    /// after a successful read.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Reads and validates one panel frame. Returns null when the map is
    /// unavailable, the header is malformed, or the bitmap shape disagrees
    /// with the panel geometry; <see cref="LastError"/> names the reason.
    /// Never throws.
    /// </summary>
    public AidaFrameSnapshot? TryReadFrame()
    {
        LastError = null;
        try
        {
            if (!_mapSource.TryRead(0, MapHeaderSize, _headerBuffer, out string? error))
            {
                LastError = error;
                return null;
            }

            if (!TryValidateMapHeader(_headerBuffer, out int width, out int height, out int bitmapSize, out int bitmapOffset))
            {
                LastError = "AIDA64 map header malformed";
                return null;
            }

            if (!_mapSource.TryRead(bitmapOffset, BmpHeaderSize, _bmpHeaderBuffer, out error))
            {
                LastError = error;
                return null;
            }

            if (!TryValidateBmpHeader(_bmpHeaderBuffer, width, height))
            {
                LastError = "AIDA64 panel bitmap header malformed";
                return null;
            }

            int payloadLength = bitmapSize - BmpPixelOffset;
            if (payloadLength != width * height * 2 || payloadLength > PanelPayloadBytes)
            {
                LastError = "AIDA64 panel bitmap size mismatch";
                return null;
            }

            if (!_mapSource.TryRead(bitmapOffset + BmpPixelOffset, payloadLength, _payloadBuffer, out error))
            {
                LastError = error;
                return null;
            }

            return new AidaFrameSnapshot(_payloadBuffer, payloadLength, width, height);
        }
        catch (Exception ex)
        {
            LastError = $"AIDA64 panel read failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Releases the underlying map source.
    /// </summary>
    public void Dispose()
    {
        _mapSource.Dispose();
    }

    internal static bool TryValidateMapHeader(byte[] header, out int width, out int height, out int bitmapSize, out int bitmapOffset)
    {
        width = height = bitmapSize = bitmapOffset = 0;
        if (header.Length < MapHeaderSize)
        {
            return false;
        }

        if (BitConverter.ToUInt32(header, MagicTagOffset) != 0xC0FFFEEEu)
        {
            return false;
        }

        // NumWidgets is the vendor Manager's REGISTERED-widget count, not a
        // liveness signal: AIDA64 still publishes a panel (its "configure the
        // LCD" placeholder when no items are set) into slot 0 while the count
        // is 0, and the vendor Manager displays it. The widget record's own
        // geometry + the embedded BMP header below are the real validation, so
        // only a negative count is malformed.
        if (BitConverter.ToInt32(header, NumWidgetsOffset) < 0)
        {
            return false;
        }

        if (BitConverter.ToInt32(header, BitmapFormatOffset) != BitmapFormatRgb565)
        {
            return false;
        }

        width = BitConverter.ToInt32(header, WidgetWidthOffset);
        height = BitConverter.ToInt32(header, WidgetHeightOffset);
        if (width <= 0 || height <= 0 || width > PanelWidth || height > PanelHeight)
        {
            return false;
        }

        bitmapSize = BitConverter.ToInt32(header, BitmapSizeOffset);
        bitmapOffset = BitConverter.ToInt32(header, BitmapOffsetOffset);
        return true;
    }

    internal static bool TryValidateBmpHeader(byte[] bmp, int width, int height)
    {
        if (bmp.Length < BmpHeaderSize)
        {
            return false;
        }

        if (bmp[0] != (byte)'B' || bmp[1] != (byte)'M')
        {
            return false;
        }

        if (BitConverter.ToInt32(bmp, 10) != BmpPixelOffset)
        {
            return false;
        }

        if (BitConverter.ToInt32(bmp, 14) != 40)
        {
            return false;
        }

        if (BitConverter.ToInt32(bmp, 18) != width || BitConverter.ToInt32(bmp, 22) != height)
        {
            return false;
        }

        if (BitConverter.ToUInt16(bmp, 28) != BmpBitCount)
        {
            return false;
        }

        if (BitConverter.ToInt32(bmp, 30) != BmpCompression)
        {
            return false;
        }

        return BitConverter.ToUInt32(bmp, 54) == Rgb565RedMask
            && BitConverter.ToUInt32(bmp, 58) == Rgb565GreenMask
            && BitConverter.ToUInt32(bmp, 62) == Rgb565BlueMask;
    }
}
