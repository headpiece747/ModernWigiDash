namespace ModernWigiDash.Widgets;

/// <summary>
/// One renderable audio frame produced by <see cref="AudioFrameBuffer.Snapshot"/>:
/// the smoothed spectrum and the chronological waveform, copied from the DSP
/// under the buffer's gate. The arrays are owned by the buffer and reused
/// across frames (double-buffered) — a consumer must draw from a frame before
/// the Snapshot two calls later overwrites that half.
/// </summary>
internal readonly record struct AudioFrame(float[] Spectrum, float[] Waveform);

/// <summary>
/// The thread-safe front of the visualizer's DSP: owns the gate around the
/// pure <see cref="AudioSpectrumAnalyzer"/> and the double-buffered output
/// copies. The capture thread feeds sample blocks; the render thread takes one
/// <see cref="Snapshot"/> per frame and draws the copy — the widget never
/// holds the gate while drawing, and no array is allocated per frame.
/// </summary>
internal sealed class AudioFrameBuffer
{
    private readonly AudioSpectrumAnalyzer _analyzer;
    private readonly Lock _gate = new();
    private readonly float[] _spectrumA;
    private readonly float[] _spectrumB;
    private readonly float[] _waveformA;
    private readonly float[] _waveformB;
    private int _frameIndex;

    /// <param name="barCount">Fixed spectrum size (see
    /// <see cref="ClampBars"/>).</param>
    /// <param name="waveformLength">Ring-buffer length for the oscilloscope.</param>
    public AudioFrameBuffer(int barCount = 64, int waveformLength = 2048)
    {
        _analyzer = new AudioSpectrumAnalyzer(barCount, waveformLength);
        _spectrumA = new float[barCount];
        _spectrumB = new float[barCount];
        _waveformA = new float[waveformLength];
        _waveformB = new float[waveformLength];
    }

    /// <summary>
    /// The single bar-count clamp — [8, capacity] — shared by the capture feed
    /// and the render draw so both sides always agree on how many bars are used.
    /// </summary>
    public int ClampBars(int requested) => Math.Clamp(requested, 8, _analyzer.BarCount);

    /// <summary>
    /// Feeds one capture block (binning + waveform ring write) under the gate.
    /// </summary>
    public void Feed(ReadOnlySpan<float> samples, int bars)
    {
        lock (_gate)
        {
            _analyzer.Analyze(samples, bars);
        }
    }

    /// <summary>
    /// Advances the smoothing once and copies the render data out under the
    /// gate into the next double-buffer half. The returned frame is stable
    /// until the following Snapshot fills the other half.
    /// </summary>
    public AudioFrame Snapshot()
    {
        lock (_gate)
        {
            _analyzer.Smooth();

            int half = _frameIndex++ & 1;
            float[] spectrum = half == 0 ? _spectrumA : _spectrumB;
            float[] waveform = half == 0 ? _waveformA : _waveformB;

            _analyzer.Spectrum.CopyTo(spectrum);
            for (int i = 0; i < _analyzer.WaveformLength; i++)
            {
                waveform[i] = _analyzer.GetWaveform(i);
            }
            return new AudioFrame(spectrum, waveform);
        }
    }
}
