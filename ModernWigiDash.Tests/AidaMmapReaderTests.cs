using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Tests;

/// <summary>
/// The AIDA64 panel reader's validation policy, driven through the
/// <see cref="IAidaMmapSource"/> seam. The map layout is the vendor's
/// <c>Aida64MmapDef</c> (WigiDashWcf.dll, decompiled 2026-09-13) and was
/// verified against the live map on-device: magic 0xC0FFFEEE at offset 8, the
/// first widget's bitmap at offset 116 as a 66-byte BMP header (RGB565
/// BI_BITFIELDS, masks F800/07E0/001F) followed by width*height*2 pixel bytes.
/// </summary>
[TestClass]
public sealed class AidaMmapReaderTests
{
    private const int BitmapOffset = 116;

    [TestMethod]
    public void TryReadFrame_ValidMap_ReturnsSnapshotWithTheWidgetGeometry()
    {
        var map = BuildValidMap(64, 32);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        var frame = reader.TryReadFrame();

        Assert.IsNotNull(frame);
        Assert.AreEqual(64, frame.Width);
        Assert.AreEqual(32, frame.Height);
        Assert.AreEqual(64 * 32 * 2, frame.PayloadLength);
        Assert.IsNull(reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_UnavailableMap_ReturnsNullWithTheSourceError()
    {
        var source = new FakeAidaMmapSource { Error = "AIDA64 map unavailable: no such map" };
        using var reader = new AidaMmapReader(source);

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 map unavailable: no such map", reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_BadMagic_ReturnsNull()
    {
        var map = BuildValidMap(64, 32);
        WriteU32(map, 8, 0xDEADBEEF);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 map header malformed", reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_ZeroWidgets_StillReadsThePublishedSlot()
    {
        // AIDA64 publishes its "no SensorPanel configured" placeholder into
        // slot 0 while the vendor Manager's registered-widget count is 0 (the
        // vendor Manager shows the same image). The reader must show it too.
        var map = BuildValidMap(64, 32);
        WriteI32(map, 16, 0);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        var frame = reader.TryReadFrame();

        Assert.IsNotNull(frame);
        Assert.AreEqual(64, frame.Width);
    }

    [TestMethod]
    public void TryReadFrame_NegativeWidgetCount_ReturnsNull()
    {
        var map = BuildValidMap(64, 32);
        WriteI32(map, 16, -1);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 map header malformed", reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_NonRgb565Format_ReturnsNull()
    {
        var map = BuildValidMap(64, 32);
        WriteI32(map, 88, 0);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 map header malformed", reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_GeometryBeyondThePanel_ReturnsNull()
    {
        var map = BuildValidMap(64, 32);
        WriteI32(map, 40, 4096);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 map header malformed", reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_BadBmpSignature_ReturnsNull()
    {
        var map = BuildValidMap(64, 32);
        map[BitmapOffset] = 0x00;
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 panel bitmap header malformed", reader.LastError);
    }

    [TestMethod]
    public void TryReadFrame_BitmapSizeMismatch_ReturnsNull()
    {
        var map = BuildValidMap(64, 32);
        WriteI32(map, 92, 66 + 10);
        using var reader = new AidaMmapReader(new FakeAidaMmapSource(map));

        Assert.IsNull(reader.TryReadFrame());
        Assert.AreEqual("AIDA64 panel bitmap size mismatch", reader.LastError);
    }

    [TestMethod]
    public void Dispose_DisposesTheMapSource()
    {
        var source = new FakeAidaMmapSource(BuildValidMap(64, 32));
        var reader = new AidaMmapReader(source);

        reader.Dispose();

        Assert.IsTrue(source.Disposed);
    }

    [TestMethod]
    public void TryReadFrame_LiveVendorMap_WhenAida64IsPublishing_ReturnsAConsistentPanel()
    {
        // Tolerant on machines without AIDA64 (the ADR-0017 absence path is the
        // placeholder tests' concern). Where the vendor Manager and AIDA64 are
        // running this drives the production adapter end-to-end: the real named
        // map open, the best-effort mutex, and a full 1.2 MB panel copy.
        using var reader = new AidaMmapReader(new MemoryMappedAidaMmapSource());

        var frame = reader.TryReadFrame();
        if (frame == null)
            return;

        Assert.IsTrue(frame.Width > 0 && frame.Height > 0);
        Assert.AreEqual(frame.Width * frame.Height * 2, frame.PayloadLength);
    }

    /// <summary>Builds a vendor-shaped map: header, first widget info, a 66-byte RGB565 BMP header, and pixels.</summary>
    private static byte[] BuildValidMap(int width, int height)
    {
        int payloadLength = width * height * 2;
        int bitmapSize = 66 + payloadLength;
        var map = new byte[BitmapOffset + bitmapSize];

        WriteU32(map, 0, 0);
        WriteU32(map, 4, 0);
        WriteU32(map, 8, 0xC0FFFEEE);
        WriteI32(map, 12, 100);
        WriteI32(map, 16, 1);
        WriteI32(map, 20, 1);
        WriteI32(map, 40, width);
        WriteI32(map, 44, height);
        WriteI32(map, 48, 5);
        WriteI32(map, 52, 4);
        WriteI32(map, 88, 135174);
        WriteI32(map, 92, bitmapSize);
        WriteI32(map, 96, BitmapOffset);

        map[BitmapOffset] = (byte)'B';
        map[BitmapOffset + 1] = (byte)'M';
        WriteI32(map, BitmapOffset + 2, bitmapSize);
        WriteI32(map, BitmapOffset + 10, 66);
        WriteI32(map, BitmapOffset + 14, 40);
        WriteI32(map, BitmapOffset + 18, width);
        WriteI32(map, BitmapOffset + 22, height);
        WriteU16(map, BitmapOffset + 26, 1);
        WriteU16(map, BitmapOffset + 28, 16);
        WriteI32(map, BitmapOffset + 30, 3);
        WriteI32(map, BitmapOffset + 34, payloadLength);
        WriteU32(map, BitmapOffset + 54, 0xF800);
        WriteU32(map, BitmapOffset + 58, 0x07E0);
        WriteU32(map, BitmapOffset + 62, 0x001F);

        for (int i = 0; i < payloadLength; i += 2)
        {
            map[BitmapOffset + 66 + i] = 0x1F;
            map[BitmapOffset + 66 + i + 1] = 0x08;
        }

        return map;
    }

    private static void WriteU32(byte[] b, int o, uint v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void WriteI32(byte[] b, int o, int v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void WriteU16(byte[] b, int o, ushort v) => BitConverter.GetBytes(v).CopyTo(b, o);
}
