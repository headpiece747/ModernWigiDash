namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// One validated panel frame from the AIDA64 shared map. A class, not a record:
/// the buffer is an array (reference equality), and a value-equality contract
/// would be false here — the bytes are the reader's reusable buffer, valid only
/// until the next <c>TryReadFrame</c>, so two snapshots over the same buffer
/// alias each other. Read the properties; do not compare snapshots or retain
/// the buffer past the call.
/// </summary>
public sealed class AidaFrameSnapshot(byte[] buffer, int pixelOffset, int payloadLength, int width, int height)
{
    /// <summary>The reader's reusable buffer: the BMP header, then the RGB565 pixels.</summary>
    public byte[] Buffer { get; } = buffer;

    /// <summary>The pixel data's offset within <see cref="Buffer"/> (past the BMP header).</summary>
    public int PixelOffset { get; } = pixelOffset;

    /// <summary>The valid pixel byte count in <see cref="Buffer"/>.</summary>
    public int PayloadLength { get; } = payloadLength;

    /// <summary>The panel width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>The panel height in pixels.</summary>
    public int Height { get; } = height;
}
