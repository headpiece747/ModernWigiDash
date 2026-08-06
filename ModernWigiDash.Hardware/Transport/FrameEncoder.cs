using SkiaSharp;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Single owner of frame encoding from an <see cref="SKBitmap"/> to the device's
/// RGB565 little-endian pixel payload. Used by both the direct (USB) and the
/// WCF frame paths so pixel encoding cannot drift between components.
/// </summary>
public static class FrameEncoder
{
    /// <summary>
    /// Converts an SKBitmap (RGBA8888) to RGB565 Little Endian byte array.
    /// Reuses a pooled buffer to reduce GC pressure at 60 FPS.
    /// </summary>
    public static void ConvertToRgb565(SKBitmap bitmap, ref byte[]? poolBuffer)
    {
        int width = DisplayProtocolConstants.FramebufferWidth;
        int height = DisplayProtocolConstants.FramebufferHeight;
        int frameSize = DisplayProtocolConstants.FrameBufferSize;

        if (poolBuffer == null || poolBuffer.Length < frameSize)
            poolBuffer = new byte[frameSize];

        byte[] rgb565 = poolBuffer;
        int idx = 0;

        int srcWidth = bitmap.Width;
        int srcHeight = bitmap.Height;

        if (srcWidth == width && srcHeight == height)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap != null && pixmap.GetPixels() != IntPtr.Zero)
            {
                unsafe
                {
                    byte* srcPtr = (byte*)pixmap.GetPixels();
                    fixed (byte* dstPtr = rgb565)
                    {
                        ushort* dstUshort = (ushort*)dstPtr;
                        int pixelCount = width * height;
                        for (int i = 0; i < pixelCount; i++)
                        {
                            byte b = srcPtr[i * 4];
                            byte g = srcPtr[i * 4 + 1];
                            byte r = srcPtr[i * 4 + 2];
                            dstUshort[i] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                        }
                    }
                }
                return;
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

                rgb565[idx++] = (byte)(rgb565Pixel & 0xFF);
                rgb565[idx++] = (byte)(rgb565Pixel >> 8);
            }
        }
    }

    /// <summary>
    /// Converts an SKBitmap (RGBA8888) to a fresh RGB565 little-endian byte array.
    /// </summary>
    public static byte[] ConvertToRgb565(SKBitmap bitmap)
    {
        byte[]? buffer = null;
        ConvertToRgb565(bitmap, ref buffer);
        return buffer!;
    }
}
