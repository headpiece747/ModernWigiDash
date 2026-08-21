using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pixel-level value assertions for the frame encoder — the fast path's
/// channel order (a red/blue swap would pass length-only tests) and the
/// scaled fallback's sampling.
/// </summary>
[TestClass]
public class FrameEncoderPixelTests
{
    private const int Width = DisplayProtocolConstants.FramebufferWidth;
    private const int Height = DisplayProtocolConstants.FramebufferHeight;

    private static ushort ReadPixel(byte[] rgb565, int x, int y)
    {
        int offset = (y * Width + x) * 2;
        return (ushort)(rgb565[offset] | (rgb565[offset + 1] << 8));
    }

    private static (byte R, byte G, byte B) Unpack(ushort pixel)
        => ((byte)((pixel >> 11) & 0x1F), (byte)((pixel >> 5) & 0x3F), (byte)(pixel & 0x1F));

    private static SKBitmap SolidBitmap(SKColorType type, byte r, byte g, byte b)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, type, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(r, g, b));
        return bitmap;
    }

    [TestMethod]
    public void FastPath_Bgra8888_EncodesExactPixelValues()
    {
        using var bitmap = SolidBitmap(SKColorType.Bgra8888, r: 255, g: 128, b: 31);
        byte[] dst = new byte[Width * Height * 2];

        FrameEncoder.ConvertToRgb565(bitmap, dst);

        var (r, g, b) = Unpack(ReadPixel(dst, Width / 2, Height / 2));
        Assert.AreEqual((byte)0x1F, r, "255 >> 3");
        Assert.AreEqual((byte)0x20, g, "128 >> 2");
        Assert.AreEqual((byte)0x03, b, "31 >> 3");
    }

    [TestMethod]
    public void FastPath_Rgba8888_KeepsChannelOrder()
    {
        // A red/blue swap would pass length-only tests — red at byte 0 here.
        using var bitmap = SolidBitmap(SKColorType.Rgba8888, r: 31, g: 64, b: 255);
        byte[] dst = new byte[Width * Height * 2];

        FrameEncoder.ConvertToRgb565(bitmap, dst);

        var (r, g, b) = Unpack(ReadPixel(dst, Width / 2, Height / 2));
        Assert.AreEqual((byte)0x03, r, "31 >> 3 — red came from byte 0, not byte 2");
        Assert.AreEqual((byte)0x10, g, "64 >> 2");
        Assert.AreEqual((byte)0x1F, b, "255 >> 3");
    }

    [TestMethod]
    public void ScaledFallback_SamplesTopLeftPixelAcrossTheBuffer()
    {
        // A 1x1 bitmap scales to fill the whole framebuffer — every pixel
        // must read the single source color.
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(255, 128, 31));
        byte[] dst = new byte[Width * Height * 2];

        FrameEncoder.ConvertToRgb565(bitmap, dst);

        var (r, g, b) = Unpack(ReadPixel(dst, 0, 0));
        Assert.AreEqual((byte)0x1F, r);
        Assert.AreEqual((byte)0x20, g);
        Assert.AreEqual((byte)0x03, b);
    }

    [TestMethod]
    public void ConvertToRgb565_DestinationTooSmall_Throws()
    {
        using var bitmap = SolidBitmap(SKColorType.Bgra8888, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => FrameEncoder.ConvertToRgb565(bitmap, new byte[8]));
    }
}
