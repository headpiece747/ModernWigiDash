using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Encodes a composited <see cref="SKBitmap"/> into the display's RGB565
/// payload, reusing a caller-owned work buffer so the 30 FPS pipeline does
/// not allocate. Implemented in Hardware where <c>FrameEncoder</c> lives;
/// <see cref="FrameDelivery"/> holds the work buffer and pool between calls.
/// </summary>
public interface IRgb565Encoder
{
    /// <summary>
    /// Fills <paramref name="workBuffer"/> (allocating when undersized) with
    /// the RGB565 little-endian bytes for <paramref name="bitmap"/>.
    /// </summary>
    void Encode(SKBitmap bitmap, ref byte[]? workBuffer);
}
