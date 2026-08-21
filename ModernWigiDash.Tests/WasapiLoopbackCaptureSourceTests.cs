using NAudio.Wave;

namespace ModernWigiDash.Tests;

/// <summary>
/// The NAudio adapter's record-buffer handling: NAudio raises DataAvailable
/// with the full recordBuffer (the device-buffer allocation) and the valid
/// region in BytesRecorded — everything beyond is zero padding. Converting the
/// whole buffer poisoned the tail bars and the 2048-sample waveform ring with
/// silence.
/// </summary>
[TestClass]
public class WasapiLoopbackCaptureSourceTests
{
    [TestMethod]
    public void ConvertRecorded_PaddedBuffer_UsesOnlyBytesRecorded()
    {
        // 8 valid 16-bit samples followed by 8 bytes of zero padding — the
        // shape of NAudio's recordBuffer (valid audio + untouched tail).
        var buffer = new byte[24];
        for (int i = 0; i < 8; i++)
        {
            short s = (short)(1000 * (i + 1));
            buffer[i * 2] = (byte)(s & 0xFF);
            buffer[i * 2 + 1] = (byte)(s >> 8);
        }

        float[]? samples = WasapiLoopbackCaptureSource.ConvertRecorded(buffer, bytesRecorded: 16, WaveFormatEncoding.Pcm, bytesPerSample: 2);

        Assert.IsNotNull(samples);
        Assert.AreEqual(8, samples.Length, "The zero padding beyond BytesRecorded must not be converted");
        Assert.AreEqual(1000f / 32768f, samples[0], 0.0001f);
        Assert.AreEqual(8000f / 32768f, samples[7], 0.0001f);
    }

    [TestMethod]
    public void ConvertRecorded_ZeroBytesRecorded_ReturnsNull()
    {
        float[]? samples = WasapiLoopbackCaptureSource.ConvertRecorded(new byte[64], bytesRecorded: 0, WaveFormatEncoding.Pcm, bytesPerSample: 2);

        Assert.IsNull(samples, "An empty recorded region must not fabricate samples from padding");
    }
}
