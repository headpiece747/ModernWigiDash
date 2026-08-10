using NAudio.Wave;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure conversion of recorded audio buffers to float samples, per the wave
/// format's encoding — IEEE-float 32-bit and PCM 16/24/32-bit (with 24-bit
/// sign extension). Extracted from the NAudio capture adapter so the
/// trickiest math (sign extension, scaling, byte order) is testable without a
/// capture device; null means "cannot convert" (unsupported encoding, empty
/// buffer, or a non-positive sample width).
/// </summary>
internal static class AudioSampleConverter
{
    public static float[]? Convert(byte[] buffer, WaveFormatEncoding encoding, int bytesPerSample)
    {
        if (bytesPerSample <= 0)
        {
            return null;
        }
        int count = buffer.Length / bytesPerSample;
        if (count <= 0)
        {
            return null;
        }

        var samples = new float[count];
        if (encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (int i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToSingle(buffer, i * bytesPerSample);
            }
        }
        else if (encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            for (int i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            }
        }
        else if (encoding == WaveFormatEncoding.Pcm && bytesPerSample == 3)
        {
            for (int i = 0; i < count; i++)
            {
                int raw = buffer[i * 3] | (buffer[i * 3 + 1] << 8) | (buffer[i * 3 + 2] << 16);
                if ((raw & 0x800000) != 0)
                {
                    raw |= unchecked((int)0xFF000000); // sign-extend 24-bit
                }
                samples[i] = raw / 8388608f;
            }
        }
        else if (encoding == WaveFormatEncoding.Pcm && bytesPerSample == 4)
        {
            for (int i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
            }
        }
        else
        {
            return null; // unsupported encoding — deliver nothing rather than garbage
        }

        return samples;
    }
}
