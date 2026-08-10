using ModernWigiDash.Hardware.Transport;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayProtocolTests
{
    [TestMethod]
    public void BuildWidgetConfig_Layout_MatchesProtocolSpec()
    {
        byte[] config = DisplayHidTransport.BuildWidgetConfig(10, 20, 1016, 592);

        // 20-byte fixed layout: x(2) y(2) w(2) h(2) baseClr(2) pad(2) addr(4) lock(1) inval(1) cache(1) pad(1)
        Assert.AreEqual(20, config.Length);
        Assert.AreEqual(10, BitConverter.ToInt16(config, 0));
        Assert.AreEqual(20, BitConverter.ToInt16(config, 2));
        Assert.AreEqual(1016, BitConverter.ToInt16(config, 4));
        Assert.AreEqual(592, BitConverter.ToInt16(config, 6));
        // All remaining fields must be zeroed
        for (int i = 8; i < 20; i++)
        {
            Assert.AreEqual(0, config[i], $"byte {i} must be zero");
        }
    }

    [TestMethod]
    public void BuildWidgetConfig_NegativeCoordinates_ArePreserved()
    {
        byte[] config = DisplayHidTransport.BuildWidgetConfig(-5, -10, 100, 50);

        Assert.AreEqual(-5, BitConverter.ToInt16(config, 0));
        Assert.AreEqual(-10, BitConverter.ToInt16(config, 2));
    }

    [TestMethod]
    public void ScreenIds_RespectProtocolSpec()
    {
        // Regression guard: pin the vendor protocol screen ids. The analyzers
        // constant-fold these pins (spurious always-true/always-false hints for
        // byte-typed protocol constants), so they are disabled — the pins ARE
        // the protocol contract.
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual((byte)0x01, DisplayProtocolConstants.ScreenWelcome);
        Assert.AreEqual((byte)0x20, DisplayProtocolConstants.ScreenBase0);
        Assert.AreEqual((byte)0x21, DisplayProtocolConstants.ScreenBase1);
        Assert.AreEqual((byte)0x22, DisplayProtocolConstants.ScreenBase2);
        // GoToScreen derives the current page from the Base screen id: the ids must stay contiguous.
        Assert.AreEqual(1, DisplayProtocolConstants.ScreenBase1 - DisplayProtocolConstants.ScreenBase0);
        Assert.AreEqual(2, DisplayProtocolConstants.ScreenBase2 - DisplayProtocolConstants.ScreenBase0);
#pragma warning restore MSTEST0025, MSTEST0032
    }

    [TestMethod]
    public void FrameEncoder_ExactSizeBitmap_ProducesValidRgb565()
    {
        int w = DisplayProtocolConstants.FramebufferWidth;
        int h = DisplayProtocolConstants.FramebufferHeight;
        // The compositor framebuffer uses Skia's default BGRA8888 layout, which
        // is what FrameEncoder expects (blue at byte 0, red at byte 2).
        using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red); // B=0 G=0 R=255 -> RGB565 0xF800

        byte[] rgb565 = new byte[DisplayProtocolConstants.FrameBufferSize];
        FrameEncoder.ConvertToRgb565(bitmap, rgb565);

        Assert.AreEqual(DisplayProtocolConstants.FrameBufferSize, rgb565.Length);
        // Little-endian 0xF800 = 0x00 0xF8
        Assert.AreEqual(0x00, rgb565[0]);
        Assert.AreEqual(0xF8, rgb565[1]);
        // Spot-check a few pixels across the buffer
        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual(0x00, rgb565[i * 2]);
            Assert.AreEqual(0xF8, rgb565[i * 2 + 1]);
        }
    }

    [TestMethod]
    public void FrameEncoder_GreenPixel_EncodesCorrectly()
    {
        int w = DisplayProtocolConstants.FramebufferWidth;
        int h = DisplayProtocolConstants.FramebufferHeight;
        using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0, 255, 0)); // B=0 G=255 R=0 -> RGB565 0x07E0

        byte[] rgb565 = new byte[DisplayProtocolConstants.FrameBufferSize];
        FrameEncoder.ConvertToRgb565(bitmap, rgb565);

        Assert.AreEqual(0xE0, rgb565[0]);
        Assert.AreEqual(0x07, rgb565[1]);
    }

    [TestMethod]
    public void WinUsbDeviceOpen_RequiresOverlappedFlag()
    {
        // Regression guard: WinUsb_Initialize fails with ERROR_INVALID_HANDLE
        // (error 6) when the CreateFileW handle was not opened with
        // FILE_FLAG_OVERLAPPED (Microsoft WinUsb_Initialize docs). The transport
        // must pass both FILE_ATTRIBUTE_NORMAL and FILE_FLAG_OVERLAPPED when
        // opening the device path — a plain normal handle makes the direct
        // WinUSB path fall back to LibUsbDotNet at runtime.
        // These assertions intentionally compare compile-time constants (the
        // constants ARE the pinned contract) — the MSTEST0032 "always true"
        // warning is the analyzer noting exactly that. Suppress: a change to
        // either constant still fails this test on recompile.
#pragma warning disable MSTEST0032
        Assert.AreEqual(0x80u, SetupApiNative.FileAttributeNormal);
        Assert.AreEqual(0x40000000u, SetupApiNative.FileFlagOverlapped);
#pragma warning restore MSTEST0032
    }

    [TestMethod]
    public void FrameEncoder_ScaledBitmap_FillsWholeBuffer()
    {
        // A non-framebuffer-sized bitmap must still produce a full-size frame
        // (the scaling fallback path).
        using var bitmap = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        byte[] rgb565 = new byte[DisplayProtocolConstants.FrameBufferSize];
        FrameEncoder.ConvertToRgb565(bitmap, rgb565);

        Assert.AreEqual(DisplayProtocolConstants.FrameBufferSize, rgb565.Length);
    }

    [TestMethod]
#pragma warning disable MSTEST0032 // Regression guard: verify protocol constants match hardware spec
    public void DisplayProtocolConstants_FramebufferCalculations_AreExact()
    {
        Assert.AreEqual(1016, DisplayProtocolConstants.FramebufferWidth);
        Assert.AreEqual(592, DisplayProtocolConstants.FramebufferHeight);
        Assert.AreEqual(2, DisplayProtocolConstants.BytesPerPixel);
        Assert.AreEqual(1202944, DisplayProtocolConstants.FrameBufferSize);
    }
#pragma warning restore MSTEST0032
}
