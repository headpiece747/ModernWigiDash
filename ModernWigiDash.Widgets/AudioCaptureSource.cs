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
            // One float per frame: 32-bit float formats use 4 bytes/frame.
            int bytesPerSample = Math.Max(4, capture.WaveFormat.BitsPerSample / 8);
            int count = e.BytesRecorded / bytesPerSample;
            if (count <= 0) return;

            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToSingle(e.Buffer, i * bytesPerSample);
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
