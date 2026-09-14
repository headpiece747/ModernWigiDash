namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// One validated panel frame from the AIDA64 shared map. The payload is the
/// reader's reusable buffer (RGB565, <see cref="PayloadLength"/> bytes), so a
/// snapshot is only valid until the next <c>TryReadFrame</c> call.
/// </summary>
public sealed record AidaFrameSnapshot(byte[] Payload, int PayloadLength, int Width, int Height);
