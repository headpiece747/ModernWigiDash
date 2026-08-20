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

    /// <summary>
    /// Converts only the valid region of NAudio's record buffer. DataAvailable
    /// carries the full device-buffer allocation with the live audio in the
    /// first <paramref name="bytesRecorded"/> bytes and zero padding after —
    /// converting the padding feeds a silence tail into the analyzer, pinning
    /// the last bars at the floor and filling the 2048-sample waveform ring
    /// with zeros.
    /// </summary>
    internal static float[]? ConvertRecorded(byte[] buffer, int bytesRecorded, WaveFormatEncoding encoding, int bytesPerSample)
        => AudioSampleConverter.Convert(buffer.AsSpan(0, Math.Min(bytesRecorded, buffer.Length)), encoding, bytesPerSample);

    public void Start()
    {
        if (_capture != null) return;

        var capture = new WasapiLoopbackCapture();
        capture.DataAvailable += (_, e) =>
        {
            var format = capture.WaveFormat;
            int bytesPerSample = format.BitsPerSample / 8;

            // The mix format is not guaranteed IEEE-float — convert per
            // encoding (the conversion is the pure AudioSampleConverter,
            // testable without a device).
            if (ConvertRecorded(e.Buffer, e.BytesRecorded, format.Encoding, bytesPerSample) is { } samples)
            {
                SamplesAvailable?.Invoke(samples);
            }
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
