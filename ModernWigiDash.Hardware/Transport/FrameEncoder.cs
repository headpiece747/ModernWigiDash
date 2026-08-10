using SkiaSharp;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Single owner of frame encoding from an <see cref="SKBitmap"/> to the device's
/// RGB565 little-endian pixel payload. Used by both the direct (USB) and the
/// frame paths so pixel encoding cannot drift between components.
/// </summary>
public static class FrameEncoder
{
    /// <summary>
    /// Converts an SKBitmap to RGB565 Little Endian, writing into the
    /// caller-provided destination buffer (which must hold at least the
    /// display framebuffer payload). The 30 FPS pipeline encodes straight
    /// into a pooled exact-size buffer, avoiding the extra copy.
    /// The zero-alloc fast path handles both BGRA8888 (SkiaSharp's
    /// PlatformColorType on Windows — byte 0 is blue) and RGBA8888 (byte 0
    /// is red) and requires tightly-packed rows; anything else takes the
    /// per-pixel fallback.
    /// </summary>
    public static void ConvertToRgb565(SKBitmap bitmap, byte[] destination)
    {
        int width = DisplayProtocolConstants.FramebufferWidth;
        int height = DisplayProtocolConstants.FramebufferHeight;
        int frameSize = DisplayProtocolConstants.FrameBufferSize;

        if (destination.Length < frameSize)
            throw new ArgumentException($"Destination buffer must hold at least {frameSize} bytes (got {destination.Length}).", nameof(destination));

        int idx = 0;

        int srcWidth = bitmap.Width;
        int srcHeight = bitmap.Height;

        if (srcWidth == width && srcHeight == height)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap != null && pixmap.GetPixels() != IntPtr.Zero)
            {
                // Fast path requires tightly-packed 4-byte rows; a padded
                // stride would skew every row. Guard color order explicitly:
                // Rgba8888 has red at byte 0, Bgra8888 has blue at byte 0.
                bool bgra = pixmap.ColorType == SKColorType.Bgra8888;
                bool rgba = pixmap.ColorType == SKColorType.Rgba8888;
                if ((bgra || rgba) && pixmap.RowBytes == width * 4)
                {
                    int redByte = rgba ? 0 : 2;
                    int blueByte = bgra ? 0 : 2;
#pragma warning disable S6640 // zero-alloc encode fast path
                    unsafe
                    {
                        byte* srcPtr = (byte*)pixmap.GetPixels();
                        fixed (byte* dstPtr = destination)
                        {
                            ushort* dstUshort = (ushort*)dstPtr;
                            int pixelCount = width * height;
                            for (int i = 0; i < pixelCount; i++)
                            {
                                byte b = srcPtr[i * 4 + blueByte];
                                byte g = srcPtr[i * 4 + 1];
                                byte r = srcPtr[i * 4 + redByte];
                                dstUshort[i] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                            }
                        }
                    }
#pragma warning restore S6640
                    return;
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            int srcY = srcHeight > 0 ? Math.Clamp((y * srcHeight) / height, 0, srcHeight - 1) : 0;
            for (int x = 0; x < width; x++)
            {
                int srcX = srcWidth > 0 ? Math.Clamp((x * srcWidth) / width, 0, srcWidth - 1) : 0;

                SKColor color = bitmap.GetPixel(srcX, srcY);
                ushort rgb565Pixel = (ushort)(((color.Red >> 3) << 11) | ((color.Green >> 2) << 5) | (color.Blue >> 3));

                destination[idx++] = (byte)(rgb565Pixel & 0xFF);
                destination[idx++] = (byte)(rgb565Pixel >> 8);
            }
        }
    }
}
