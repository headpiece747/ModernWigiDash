using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Encodes a composited <see cref="SKBitmap"/> into the display's RGB565
/// payload, writing directly into a caller-owned destination buffer so the
/// 30 FPS pipeline does not allocate. Implemented in Hardware where
/// <c>FrameEncoder</c> lives; <see cref="FrameDelivery"/> owns the pooled
/// exact-size buffers it encodes into.
/// </summary>
public interface IRgb565Encoder
{
    /// <summary>
    /// Fills <paramref name="destination"/> with the RGB565 little-endian
    /// bytes for <paramref name="bitmap"/>. <paramref name="destination"/>
    /// must hold the display's exact framebuffer payload (1016×592 RGB565).
    /// </summary>
    void Encode(SKBitmap bitmap, byte[] destination);
}
