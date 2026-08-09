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
    /// Converts an SKBitmap (RGBA8888) to RGB565 Little Endian, writing into
    /// the caller-provided destination buffer (which must hold at least the
    /// display framebuffer payload). The 30 FPS pipeline encodes straight
    /// into a pooled exact-size buffer, avoiding the extra copy.
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
                            byte b = srcPtr[i * 4];
                            byte g = srcPtr[i * 4 + 1];
                            byte r = srcPtr[i * 4 + 2];
                            dstUshort[i] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                        }
                    }
                }
#pragma warning restore S6640
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

                destination[idx++] = (byte)(rgb565Pixel & 0xFF);
                destination[idx++] = (byte)(rgb565Pixel >> 8);
            }
        }
    }
}
