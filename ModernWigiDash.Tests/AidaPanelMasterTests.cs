using ModernWigiDash.Hardware.Aida64;

namespace ModernWigiDash.Tests;

/// <summary>
/// The AIDA64 panel master's vendor protocol: slot registration (the header plus
/// one 5x4 widget record), the heartbeat/slave/marker poll, the published copy,
/// and the frame ack. Driven through the read/write seams, so no map and no
/// service are involved. The protocol is the one the vendor Manager runs
/// (verified against the live service 2026-09-14: registering + acking makes
/// AIDA64 publish ~1 frame a second).
/// </summary>
[TestClass]
public sealed class AidaPanelMasterTests
{
    [TestMethod]
    public void EnsureRegistered_WritesTheHeaderAndTheWidgetRecord()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        Assert.IsTrue(master.EnsureRegistered());
        Assert.IsTrue(master.IsRegistered);

        var block = WriteAt(writer, AidaPanelMaster.MapBlockOffset);
        Assert.IsNotNull(block);
        Assert.AreEqual(AidaPanelMaster.MapBlockSize, block.Length);
        Assert.AreEqual(AidaPanelMaster.MmapMagicTag, BitConverter.ToUInt32(block, 0), "magic tag");
        Assert.AreEqual(AidaPanelMaster.WidgetVersion, BitConverter.ToInt32(block, 4));
        Assert.AreEqual(1, BitConverter.ToInt32(block, 8), "NumWidgets");
        Assert.AreEqual(1, BitConverter.ToInt32(block, 12), "ConfigUpdated");
        Assert.AreEqual(1016, BitConverter.ToInt32(block, 32), "registered widget width");
        Assert.AreEqual(592, BitConverter.ToInt32(block, 36), "registered widget height");
        Assert.AreEqual(5, BitConverter.ToInt32(block, 40), "width tiles");
        Assert.AreEqual(4, BitConverter.ToInt32(block, 44), "height tiles");
        Assert.AreEqual(135174, BitConverter.ToInt32(block, 80), "RGB565 format");
        Assert.AreEqual(AidaPanelMaster.BitmapSize, BitConverter.ToInt32(block, 84), "bitmap size");
        Assert.AreEqual(AidaPanelMaster.BitmapOffset, BitConverter.ToInt32(block, 88), "bitmap offset");
    }

    [TestMethod]
    public void EnsureRegistered_InitFails_ReturnsFalseAndWritesNothing()
    {
        var writer = new FakeAidaMmapWriter { InitResult = false };
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        Assert.IsFalse(master.EnsureRegistered());
        Assert.IsFalse(master.IsRegistered);
        Assert.AreEqual(0, writer.Writes.Count);
    }

    [TestMethod]
    public void EnsureRegistered_IsIdempotent()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        Assert.IsTrue(master.EnsureRegistered());
        Assert.IsTrue(master.EnsureRegistered());

        Assert.AreEqual(1, writer.InitCalls);
        Assert.AreEqual(1, writer.Writes.Count(w => w.Offset == AidaPanelMaster.MapBlockOffset));
    }

    [TestMethod]
    public void PollOnce_NewFrameMarker_PublishesACopyAndAcks()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();

        var published = master.TryGetPublished();
        Assert.IsNotNull(published, "a set marker is a new frame");
        Assert.AreEqual(64, published.Width);
        Assert.AreEqual(32, published.Height);
        Assert.AreEqual(64 * 32 * 2, published.Length);
        Assert.AreEqual(1, published.Generation);
        Assert.AreEqual(0x1F, published.Pixels[0], "the published copy starts at the pixels, not the BMP header");
        Assert.AreEqual(0x08, published.Pixels[1]);

        var ack = WriteAt(writer, AidaPanelMaster.BitmapOffset);
        Assert.IsNotNull(ack, "the frame must be acknowledged so AIDA64 writes the next one");
        CollectionAssert.AreEqual(new byte[4], ack, "the ack writes zero to the marker");
    }

    [TestMethod]
    public void PollOnce_ZeroMarker_DoesNotPublish()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32, markerSet: false)), writer);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();

        Assert.IsNull(master.TryGetPublished());
        Assert.IsNull(WriteAt(writer, AidaPanelMaster.BitmapOffset), "a consumed slot is not acked again");
    }

    [TestMethod]
    public void PollOnce_BeforeTheSlaveMoves_DoesNotConsume()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32, slaveCounter: 0)), writer);
        Assert.IsTrue(master.EnsureRegistered());

        master.PollOnce();

        Assert.IsNull(master.TryGetPublished(), "a slot AIDA64 has never written is not consumed");
        Assert.IsNull(WriteAt(writer, AidaPanelMaster.BitmapOffset));
    }

    [TestMethod]
    public void PollOnce_BeforeRegistration_DoesNothing()
    {
        var writer = new FakeAidaMmapWriter();
        using var master = Create(new FakeAidaMmapSource(AidaTestMap.BuildValid(64, 32)), writer);

        master.PollOnce();

        Assert.IsNull(master.TryGetPublished());
        Assert.AreEqual(0, writer.Writes.Count, "no heartbeat and no ack before the slot is registered");
    }

    private static AidaPanelMaster Create(IAidaMmapSource source, FakeAidaMmapWriter writer)
        => new(source, writer, log: static _ => { });

    private static byte[]? WriteAt(FakeAidaMmapWriter writer, int offset)
    {
        foreach (var write in writer.Writes)
        {
            if (write.Offset == offset)
            {
                return write.Data;
            }
        }

        return null;
    }
}
