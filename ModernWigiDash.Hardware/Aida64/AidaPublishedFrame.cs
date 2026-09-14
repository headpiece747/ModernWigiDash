namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// An immutable published AIDA64 frame: the pixels in a stable buffer (the
/// master's ring, not reused until the ring wraps), the geometry, and a
/// monotonic generation. Consumers rebuild their bitmap only when the
/// generation changes. Reading a copy — never the map slot — matters because the
/// master's frame ack zeroes the slot's BMP signature.
/// </summary>
public sealed record AidaPublishedFrame(byte[] Pixels, int Length, int Width, int Height, long Generation);
