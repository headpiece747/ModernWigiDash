namespace ModernWigiDash.Widgets;

/// <summary>
/// The pure signal-processing core behind the visualizer: bins mono float
/// samples into a spectrum, keeps an exponentially-smoothed spectrum, and
/// maintains the waveform ring buffer. No capture, no rendering — any sample
/// source (WASAPI adapter, test fake) can drive it, and the binning math is
/// testable without audio hardware.
/// </summary>
internal sealed class AudioSpectrumAnalyzer
{
    private readonly float[] _fftSpectrum;
    private readonly float[] _smoothSpectrum;
    private readonly float[] _waveform;
    private int _waveformHead;

    /// <param name="barCount">Fixed spectrum size (the widget clamps its
    /// BarCount property to ≤ this).</param>
    /// <param name="waveformLength">Ring-buffer length for the oscilloscope.</param>
    public AudioSpectrumAnalyzer(int barCount = 64, int waveformLength = 2048)
    {
        _fftSpectrum = new float[barCount];
        _smoothSpectrum = new float[barCount];
        _waveform = new float[waveformLength];
    }

    public int BarCount => _smoothSpectrum.Length;
    public int WaveformLength => _waveform.Length;

    /// <summary>The smoothed spectrum, ready for rendering.</summary>
    public ReadOnlySpan<float> Spectrum => _smoothSpectrum;

    /// <summary>
    /// Bins a sample block into the spectrum (absolute-value averaging per
    /// bar, clamped to [0.05, 1]) and appends the samples to the waveform ring
    /// buffer. Not thread-safe — callers guard with a lock.
    /// </summary>
    public void Analyze(ReadOnlySpan<float> samples, int requestedBars)
    {
        if (samples.IsEmpty) return;

        int bars = Math.Clamp(requestedBars, 8, _smoothSpectrum.Length);
        int samplesPerBar = Math.Max(1, samples.Length / bars);

        for (int i = 0; i < bars; i++)
        {
            float barSum = 0f;
            for (int j = 0; j < samplesPerBar; j++)
            {
                int index = i * samplesPerBar + j;
                if (index < samples.Length)
                {
                    barSum += Math.Abs(samples[index]);
                }
            }

            _fftSpectrum[i] = Math.Clamp((barSum / samplesPerBar) * 8.0f, 0.05f, 1f);
        }

        foreach (float sample in samples)
        {
            _waveform[_waveformHead] = sample;
            _waveformHead = (_waveformHead + 1) % _waveform.Length;
        }
    }

    /// <summary>
    /// Blends the current spectrum into the smoothed one (exponential
    /// smoothing — call once per render).
    /// </summary>
    public void Smooth(float alpha = 0.60f, float blend = 0.40f)
    {
        for (int i = 0; i < _smoothSpectrum.Length; i++)
        {
            _smoothSpectrum[i] = _smoothSpectrum[i] * alpha + _fftSpectrum[i] * blend;
        }
    }

    /// <summary>Reads the waveform ring buffer in chronological order (oldest
    /// first), clamped to [-1, 1] for rendering.</summary>
    public float GetWaveform(int index) => Math.Clamp(_waveform[(_waveformHead + index) % _waveform.Length], -1f, 1f);
}
