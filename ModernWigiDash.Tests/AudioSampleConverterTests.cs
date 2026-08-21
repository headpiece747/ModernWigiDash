using NAudio.Wave;

namespace ModernWigiDash.Tests;

/// <summary>
/// The audio-buffer → float conversion — previously buried inside the NAudio
/// capture adapter and untestable without a device. The 24-bit sign extension
/// and the scaling boundaries are the classic silent-garbled-audio bugs.
/// </summary>
[TestClass]
public class AudioSampleConverterTests
{
    [TestMethod]
    public void Convert_IeeeFloat32_RoundTrips()
    {
        byte[] buffer = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(buffer, 0);
        BitConverter.GetBytes(-1.0f).CopyTo(buffer, 4);
        BitConverter.GetBytes(0.25f).CopyTo(buffer, 8);
        BitConverter.GetBytes(1.0f).CopyTo(buffer, 12);

        var samples = AudioSampleConverter.Convert(buffer, WaveFormatEncoding.IeeeFloat, 4);

        CollectionAssert.AreEqual(new[] { 0.5f, -1.0f, 0.25f, 1.0f }, samples);
    }

    [TestMethod]
    public void Convert_Pcm16_SignScalesToUnitRange()
    {
        byte[] buffer =
        [
            0xFF, 0x7F, // 32767 → ~1.0
            0x00, 0x80, // -32768 → -1.0
            0x00, 0x00, // 0 → 0
            0xCD, 0x3C, // 15565 → ~0.475
        ];

        var samples = AudioSampleConverter.Convert(buffer, WaveFormatEncoding.Pcm, 2);

        Assert.AreEqual(4, samples!.Length);
        Assert.AreEqual(32767f / 32768f, samples[0], 0.0001f);
        Assert.AreEqual(-1.0f, samples[1], 0.0001f);
        Assert.AreEqual(0.0f, samples[2], 0.0001f);
        Assert.AreEqual(15565f / 32768f, samples[3], 0.0001f);
    }

    [TestMethod]
    public void Convert_Pcm24_SignExtendsNegativeSamples()
    {
        // -1 in 24-bit two's complement: 0xFFFFFF, little-endian.
        byte[] buffer = [0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x00, 0x80];

        var samples = AudioSampleConverter.Convert(buffer, WaveFormatEncoding.Pcm, 3);

        Assert.AreEqual(3, samples!.Length);
        Assert.AreEqual(-1f / 8388608f, samples[0], 0.0001f, "0xFFFFFF is -1 LSB — the sign extension keeps it negative");
        Assert.AreEqual(1f / 8388608f, samples[1], 0.0001f, "0x000001 stays positive");
        Assert.AreEqual(-1.0f, samples[2], 0.0001f, "0x800000 is the 24-bit minimum");
    }

    [TestMethod]
    public void Convert_Pcm32_SignScalesToUnitRange()
    {
        byte[] buffer = new byte[8];
        BitConverter.GetBytes(int.MaxValue).CopyTo(buffer, 0);
        BitConverter.GetBytes(int.MinValue).CopyTo(buffer, 4);

        var samples = AudioSampleConverter.Convert(buffer, WaveFormatEncoding.Pcm, 4);

        Assert.AreEqual(2, samples!.Length);
        Assert.AreEqual(1.0f, samples[0], 0.0001f);
        Assert.AreEqual(-1.0f, samples[1], 0.0001f);
    }

    [TestMethod]
    public void Convert_UnsupportedEncoding_ReturnsNull()
    {
        Assert.IsNull(AudioSampleConverter.Convert(new byte[8], WaveFormatEncoding.ALaw, 1), "8-bit/ALaw is unsupported — null beats garbage");
        Assert.IsNull(AudioSampleConverter.Convert(new byte[8], WaveFormatEncoding.IeeeFloat, 2), "float at 2 bytes is unsupported");
    }

    [TestMethod]
    public void Convert_EmptyBuffer_ReturnsNull_PartialTrailingByteDropped()
    {
        Assert.IsNull(AudioSampleConverter.Convert([], WaveFormatEncoding.Pcm, 2));
        Assert.AreEqual(1, AudioSampleConverter.Convert(new byte[3], WaveFormatEncoding.Pcm, 2)!.Length,
            "the trailing partial byte is dropped, the complete sample converts");
    }

    [TestMethod]
    public void Convert_NonPositiveSampleWidth_ReturnsNull()
    {
        Assert.IsNull(AudioSampleConverter.Convert(new byte[8], WaveFormatEncoding.Pcm, 0));
    }
}
