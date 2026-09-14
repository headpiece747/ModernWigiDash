namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// One validated panel frame from the AIDA64 shared map. A class, not a record:
/// the payload is an array (reference equality), and a value-equality contract
/// would be false here — the bytes are the reader's reusable buffer, valid only
/// until the next <c>TryReadFrame</c>, so two snapshots over the same buffer
/// alias each other. Read the properties; do not compare snapshots or retain
/// the payload past the call.
/// </summary>
public sealed class AidaFrameSnapshot(byte[] payload, int payloadLength, int width, int height)
{
    /// <summary>The reader's reusable RGB565 buffer, valid until the next read.</summary>
    public byte[] Payload { get; } = payload;

    /// <summary>The valid byte count in <see cref="Payload"/>.</summary>
    public int PayloadLength { get; } = payloadLength;

    /// <summary>The panel width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>The panel height in pixels.</summary>
    public int Height { get; } = height;
}
