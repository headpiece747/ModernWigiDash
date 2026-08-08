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
}
