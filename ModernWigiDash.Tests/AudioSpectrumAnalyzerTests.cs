
namespace ModernWigiDash.Tests;

/// <summary>
/// The pure DSP core behind the audio visualizer — binning, smoothing, ring
/// buffer — driven without any audio hardware.
/// </summary>
[TestClass]
public class AudioSpectrumAnalyzerTests
{
    [TestMethod]
    public void Analyze_Silence_ProducesMinimalBars()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        float[] silence = new float[1024];

        analyzer.Analyze(silence, requestedBars: 32);
        analyzer.Smooth();

        // The raw floor is 0.05; one smooth pass blends it to 0.05 * 0.4.
        foreach (float bar in analyzer.Spectrum)
        {
            Assert.IsTrue(bar >= 0f, $"Bars must never go negative (got {bar})");
            Assert.IsTrue(bar <= 1f);
            Assert.IsTrue(bar <= 0.03f, $"Silence must stay near the smoothed floor (got {bar})");
        }
    }

    [TestMethod]
    public void Analyze_LoudSignal_ProducesHigherBars()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        float[] loud = Enumerable.Repeat(0.9f, 1024).ToArray();
        float[] quiet = Enumerable.Repeat(0.01f, 1024).ToArray();

        analyzer.Analyze(loud, requestedBars: 32);
        analyzer.Smooth();
        float loudBar = analyzer.Spectrum[0];

        analyzer.Analyze(quiet, requestedBars: 32);
        analyzer.Smooth();
        float quietBar = analyzer.Spectrum[0];

        Assert.IsTrue(loudBar > quietBar, "A louder signal must produce a higher smoothed bar");
    }

    [TestMethod]
    public void Smooth_BlendsNewSpectrumIntoOld()
    {
        var analyzer = new AudioSpectrumAnalyzer();
        analyzer.Analyze([0.5f], requestedBars: 8); // raw bar: 0.5 * 8 clamped to 1.0
        analyzer.Smooth();
        float afterFirst = analyzer.Spectrum[0];
        Assert.AreEqual(0.4f, afterFirst, 0.001f, "First smooth pass retains 40% of the raw bar");

        analyzer.Analyze([0.02f], requestedBars: 8); // raw bar: 0.16 — below the old smoothed level
        analyzer.Smooth();

        // 0.16 blends only 40% in: the smoothed bar must sit between the raw
        // new value and the old smoothed value.
        Assert.IsTrue(analyzer.Spectrum[0] < afterFirst, "Smoothing must damp a drop");
        Assert.IsTrue(analyzer.Spectrum[0] > 0.16f, "Smoothing must retain a share of the previous level");
    }

    [TestMethod]
    public void Analyze_ClampsRequestedBars()
    {
        var analyzer = new AudioSpectrumAnalyzer(barCount: 64);
        float[] samples = new float[512];

        analyzer.Analyze(samples, requestedBars: 200); // above the fixed size

        Assert.AreEqual(64, analyzer.BarCount);
        Assert.AreEqual(64, analyzer.Spectrum.Length, "The bar count must never exceed the fixed spectrum size");
    }

    [TestMethod]
    public void Waveform_RingBuffer_KeepsLatestSamplesInOrder()
    {
        var analyzer = new AudioSpectrumAnalyzer(waveformLength: 64);
        float[] samples = new float[128];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % 64) / 64f; // 0 .. 0.984 — inside the render clamp
        }

        analyzer.Analyze(samples, requestedBars: 8);

        // The ring holds the LAST 64 samples; reading in order returns them
        // oldest-first — the first read is sample #64, the last is #127.
        Assert.AreEqual(0f, analyzer.GetWaveform(0));
        Assert.AreEqual(63f / 64f, analyzer.GetWaveform(63), 0.001f);
    }

    [TestMethod]
    public void Analyze_EmptySamples_IsNoOp()
    {
        var analyzer = new AudioSpectrumAnalyzer();

        analyzer.Analyze([], requestedBars: 16);
        analyzer.Smooth();

        Assert.IsTrue(analyzer.Spectrum.ToArray().All(v => v == 0f), "No input must leave the spectrum untouched");
    }
}
