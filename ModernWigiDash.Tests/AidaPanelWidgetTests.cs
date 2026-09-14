using ModernWigiDash.Hardware.Aida64;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The AIDA64 panel widget: it draws the frame its reader returns, and degrades
/// to the house placeholder (never a throw) when AIDA64 is not publishing.
/// The reader's own validation policy is pinned in <see cref="AidaMmapReaderTests"/>.
/// </summary>
[TestClass]
public sealed class AidaPanelWidgetTests
{
    [TestMethod]
    public void Render_NoMap_ProducesCanvas()
    {
        using var reader = new AidaMmapReader(new FakeAidaMmapSource());
        var widget = new AidaPanelWidget(reader);
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);

        widget.Render(canvas, new SKRect(0, 0, 200, 100));

        Assert.AreEqual(200, bitmap.Width);
    }

    [TestMethod]
    public void Render_WithFrame_ProducesCanvas()
    {
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(BuildValidMap(1016, 592)));
        var widget = new AidaPanelWidget(reader);
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);

        widget.Render(canvas, new SKRect(0, 0, 200, 100));

        Assert.AreEqual(100, bitmap.Height);
    }

    [TestMethod]
    public void Render_MalformedMap_ProducesCanvas()
    {
        var map = BuildValidMap(1016, 592);
        map[116] = 0x00; // break the BMP signature
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));
        var widget = new AidaPanelWidget(reader);
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);

        widget.Render(canvas, new SKRect(0, 0, 200, 100));

        Assert.AreEqual(100, bitmap.Height);
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesTheReaderAndItsMapSource()
    {
        var source = new FakeAidaMmapSource(BuildValidMap(64, 32));
        var widget = new AidaPanelWidget(new AidaMmapReader(source));

        await widget.DisposeAsync();

        Assert.IsTrue(source.Disposed);
    }

    [TestMethod]
    public void BuildBitmap_BottomUpPayload_IsFlippedToTopDown()
    {
        // A 2x2 panel. BMP file row 0 is the image BOTTOM row, so the payload
        // is [red row][black row]; the built bitmap's top row must be black and
        // its bottom row red (the on-device bug drew the panel upside down).
        byte[] payload = [0x00, 0xF8, 0x00, 0xF8, 0x00, 0x00, 0x00, 0x00];

        using var bitmap = AidaPanelWidget.BuildBitmap(payload, payload.Length, 2, 2);

        Assert.IsNotNull(bitmap);
        Assert.AreEqual(SKColors.Black, bitmap.GetPixel(0, 0));
        Assert.AreEqual(new SKColor(255, 0, 0), bitmap.GetPixel(0, 1));
    }

    private static byte[] BuildValidMap(int width, int height)
    {
        const int bitmapOffset = 116;
        int payloadLength = width * height * 2;
        int bitmapSize = 66 + payloadLength;
        var map = new byte[bitmapOffset + bitmapSize];

        BitConverter.GetBytes(0xC0FFFEEEu).CopyTo(map, 8);
        BitConverter.GetBytes(1).CopyTo(map, 16);
        BitConverter.GetBytes(width).CopyTo(map, 40);
        BitConverter.GetBytes(height).CopyTo(map, 44);
        BitConverter.GetBytes(135174).CopyTo(map, 88);
        BitConverter.GetBytes(bitmapSize).CopyTo(map, 92);
        BitConverter.GetBytes(bitmapOffset).CopyTo(map, 96);

        map[bitmapOffset] = (byte)'B';
        map[bitmapOffset + 1] = (byte)'M';
        BitConverter.GetBytes(66).CopyTo(map, bitmapOffset + 10);
        BitConverter.GetBytes(40).CopyTo(map, bitmapOffset + 14);
        BitConverter.GetBytes(width).CopyTo(map, bitmapOffset + 18);
        BitConverter.GetBytes(height).CopyTo(map, bitmapOffset + 22);
        BitConverter.GetBytes((ushort)16).CopyTo(map, bitmapOffset + 28);
        BitConverter.GetBytes(3).CopyTo(map, bitmapOffset + 30);
        BitConverter.GetBytes(0xF800u).CopyTo(map, bitmapOffset + 54);
        BitConverter.GetBytes(0x07E0u).CopyTo(map, bitmapOffset + 58);
        BitConverter.GetBytes(0x001Fu).CopyTo(map, bitmapOffset + 62);

        for (int i = 0; i < payloadLength; i += 2)
        {
            map[bitmapOffset + 66 + i] = 0x1F;
            map[bitmapOffset + 66 + i + 1] = 0x08;
        }

        return map;
    }
}
