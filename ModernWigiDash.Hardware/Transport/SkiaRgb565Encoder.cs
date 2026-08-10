using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Adapter exposing <see cref="FrameEncoder"/> behind the Sdk encoder seam so
/// <see cref="FrameDelivery"/> can own encoding without referencing Hardware.
/// </summary>
public sealed class SkiaRgb565Encoder : IRgb565Encoder
{
    /// <inheritdoc />
    public int OutputBufferSize => DisplayProtocolConstants.FrameBufferSize;

    /// <inheritdoc />
    public void Encode(SKBitmap bitmap, byte[] destination)
        => FrameEncoder.ConvertToRgb565(bitmap, destination);
}
