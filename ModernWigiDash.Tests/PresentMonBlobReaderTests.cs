using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

[TestClass]
public class PresentMonBlobReaderTests
{
    private static PresentMonQueryElement DoubleAt(ulong dataOffset) =>
        new(Metric: 0, Stat: 0, DeviceId: 0, ArrayIndex: 0, DataOffset: dataOffset, DataSize: 8);

    [TestMethod]
    public void ChainStrideBytes_SumsElementDataSizes()
    {
        PresentMonQueryElement[] elements =
        [
            DoubleAt(0),
            DoubleAt(8),
            new(Metric: 0, Stat: 0, DeviceId: 0, ArrayIndex: 0, DataOffset: 16, DataSize: 4),
        ];

        Assert.AreEqual(20, PresentMonBlobReader.ChainStrideBytes(elements));
    }

    [TestMethod]
    public void ReadDynamicDouble_FirstChain_ReadsAtElementDataOffset()
    {
        var element = DoubleAt(16);
        byte[] blob = new byte[24];
        BitConverter.GetBytes(123.5).CopyTo(blob, 16);

        double value = PresentMonBlobReader.ReadDynamicDouble(blob, chainIndex: 0, chainStrideBytes: 24, element);

        Assert.AreEqual(123.5, value);
    }

    [TestMethod]
    public void ReadDynamicDouble_SecondChain_StepsPastFirstChain()
    {
        var element = DoubleAt(16);
        byte[] blob = new byte[24 * 3];
        BitConverter.GetBytes(77.0).CopyTo(blob, 24 + 16);

        double value = PresentMonBlobReader.ReadDynamicDouble(blob, chainIndex: 1, chainStrideBytes: 24, element);

        Assert.AreEqual(77.0, value);
    }

    [TestMethod]
    public void ReadFrameDouble_ReadsAtElementDataOffsetWithinFrameBlob()
    {
        var element = DoubleAt(8);
        byte[] frameBlob = new byte[16];
        BitConverter.GetBytes(6.98).CopyTo(frameBlob, 8);

        double value = PresentMonBlobReader.ReadFrameDouble(frameBlob, element);

        Assert.AreEqual(6.98, value);
    }

    [TestMethod]
    public void ReadDynamicDouble_OffsetBeyondBlob_Throws()
    {
        var element = DoubleAt(24);
        byte[] blob = new byte[24];

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        {
            PresentMonBlobReader.ReadDynamicDouble(blob, chainIndex: 0, chainStrideBytes: 24, element);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void ReadDynamicElement_Int32SizedElement_ReadsInt32NotDouble()
    {
        // PRESENT_MODE is enum-typed: the service stores it as 4 bytes (the
        // element's DataSize reports 4). Reading it as an 8-byte double would
        // swallow the following element's bytes and produce garbage.
        var element = new PresentMonQueryElement(Metric: 20, Stat: 12, DeviceId: 0, ArrayIndex: 0, DataOffset: 8, DataSize: 4);
        byte[] blob = new byte[20];
        BitConverter.GetBytes(8).CopyTo(blob, 8);
        BitConverter.GetBytes(99.5).CopyTo(blob, 12); // next element's double

        double value = PresentMonBlobReader.ReadDynamicElement(blob, chainIndex: 0, chainStrideBytes: 20, element);

        Assert.AreEqual(8.0, value, "the enum id must read as its int32 value, not a garbage double");
    }

    [TestMethod]
    public void ReadDynamicElement_DoubleSizedElement_ReadsDouble()
    {
        var element = DoubleAt(8);
        byte[] blob = new byte[16];
        BitConverter.GetBytes(143.2).CopyTo(blob, 8);

        double value = PresentMonBlobReader.ReadDynamicElement(blob, chainIndex: 0, chainStrideBytes: 16, element);

        Assert.AreEqual(143.2, value);
    }
}
