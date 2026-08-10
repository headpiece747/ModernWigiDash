using NAudio.Wave;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The audio-capture seam behind the visualizer: a source of mono float
/// samples from the system audio output, drivable by an in-memory fake in
/// tests. The widget renders snapshots and never touches WASAPI; capture
/// lifecycle (start/stop) is the source's job.
/// </summary>
internal interface IAudioCaptureSource : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>Delivers a block of interleaved float samples (one per
    /// 4-byte frame, matching the device format). Raised on the capture
    /// thread; the consumer must not block.</summary>
    event Action<float[]>? SamplesAvailable;

    void Start();

    void Stop();
}

/// <summary>
/// <see cref="IAudioCaptureSource"/> adapter over NAudio's
/// <see cref="WasapiLoopbackCapture"/>: captures the system output mix and
/// converts each recorded frame to a float sample.
/// </summary>
internal sealed class WasapiLoopbackCaptureSource : IAudioCaptureSource
{
    private WasapiLoopbackCapture? _capture;

    public bool IsCapturing => _capture != null;

    public event Action<float[]>? SamplesAvailable;

    public void Start()
    {
        if (_capture != null) return;

        var capture = new WasapiLoopbackCapture();
        capture.DataAvailable += (_, e) =>
        {
            var format = capture.WaveFormat;
            int bytesPerSample = format.BitsPerSample / 8;
            if (bytesPerSample <= 0) return;

            // The mix format is not guaranteed IEEE-float: on PCM devices the
            // old code read 4-byte floats over 2-byte samples and produced
            // garbage. Convert per encoding instead.
            int count = e.BytesRecorded / bytesPerSample;
            if (count <= 0) return;

            var samples = new float[count];
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
            {
                for (int i = 0; i < count; i++)
                {
                    samples[i] = BitConverter.ToSingle(e.Buffer, i * bytesPerSample);
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
            {
                for (int i = 0; i < count; i++)
                {
                    samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 3)
            {
                for (int i = 0; i < count; i++)
                {
                    int raw = e.Buffer[i * 3] | (e.Buffer[i * 3 + 1] << 8) | (e.Buffer[i * 3 + 2] << 16);
                    if ((raw & 0x800000) != 0) raw |= unchecked((int)0xFF000000); // sign-extend 24-bit
                    samples[i] = raw / 8388608f;
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 4)
            {
                for (int i = 0; i < count; i++)
                {
                    samples[i] = BitConverter.ToInt32(e.Buffer, i * 4) / 2147483648f;
                }
            }
            else
            {
                return; // unsupported encoding — deliver nothing rather than garbage
            }

            SamplesAvailable?.Invoke(samples);
        };

        capture.StartRecording();
        _capture = capture;
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return;

        try
        {
            capture.StopRecording();
        }
        catch
        {
            // Capture may already have failed/stopped on the device side
        }
        capture.Dispose();
    }

    public void Dispose() => Stop();
}
