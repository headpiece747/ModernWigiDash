using System.Collections.Concurrent;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The visualizer's thread-safe front: Feed on the capture side, one Snapshot
/// per frame on the render side, double-buffered output copies so the draw
/// never holds the gate and no array is allocated per frame.
/// </summary>
[TestClass]
public class AudioFrameBufferTests
{
    [TestMethod]
    public void Snapshot_AfterFeed_ReturnsSmoothedSpectrum()
    {
        var buffer = new AudioFrameBuffer(barCount: 8);
        buffer.Feed([0.5f], bars: 8); // raw bar: 0.5 * 8 clamped to 1.0

        AudioFrame frame = buffer.Snapshot();

        // One smooth pass retains 40% of the raw bar (the analyzer's math).
        Assert.AreEqual(0.4f, frame.Spectrum[0], 0.001f);
    }

    [TestMethod]
    public void Snapshot_WithoutFeed_AllZero()
    {
        var buffer = new AudioFrameBuffer(barCount: 8);

        AudioFrame frame = buffer.Snapshot();

        Assert.IsTrue(frame.Spectrum.All(v => v == 0f), "No input must leave the spectrum untouched");
    }

    [TestMethod]
    public void Snapshot_AdvancesSmoothingPerCall()
    {
        var buffer = new AudioFrameBuffer(barCount: 8);
        buffer.Feed([0.9f], bars: 8);

        AudioFrame first = buffer.Snapshot();
        AudioFrame second = buffer.Snapshot();

        // 0.4 then 0.4*0.6 + 0.4 — each frame blends another pass toward the raw.
        Assert.IsTrue(second.Spectrum[0] > first.Spectrum[0], "Each Snapshot must advance the smoothing once");
        Assert.IsTrue(second.Spectrum[0] <= 1f);
    }

    [TestMethod]
    public void Snapshot_Waveform_IsChronologicalRingCopy()
    {
        var buffer = new AudioFrameBuffer(barCount: 8, waveformLength: 64);
        float[] samples = new float[128];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % 64) / 64f; // 0 .. 0.984 — inside the render clamp
        }

        buffer.Feed(samples, bars: 8);

        AudioFrame frame = buffer.Snapshot();
        Assert.AreEqual(64, frame.Waveform.Length);
        Assert.AreEqual(0f, frame.Waveform[0], "Oldest sample first — sample #64");
        Assert.AreEqual(63f / 64f, frame.Waveform[63], 0.001f, "Newest sample last — sample #127");
    }

    [TestMethod]
    public void Snapshot_DoubleBuffers_AlternateAndReuseArrays()
    {
        var buffer = new AudioFrameBuffer(barCount: 8);
        buffer.Feed([0.5f], bars: 8);

        AudioFrame a = buffer.Snapshot();
        AudioFrame b = buffer.Snapshot();
        AudioFrame c = buffer.Snapshot();

        // Consecutive frames never share arrays; the third reuses the first
        // half — the snapshot is allocation-free (same instances, no new
        // arrays per frame).
        Assert.IsFalse(ReferenceEquals(a.Spectrum, b.Spectrum), "Consecutive frames must not share the spectrum buffer");
        Assert.IsFalse(ReferenceEquals(a.Waveform, b.Waveform), "Consecutive frames must not share the waveform buffer");
        Assert.IsTrue(ReferenceEquals(a.Spectrum, c.Spectrum), "The double buffer must alternate (frame N and N+2 reuse)");
        Assert.IsTrue(ReferenceEquals(a.Waveform, c.Waveform), "The double buffer must alternate (frame N and N+2 reuse)");
    }

    [TestMethod]
    public void ClampBars_ClampsIntoSingleRange()
    {
        var buffer = new AudioFrameBuffer(barCount: 64);

        Assert.AreEqual(64, buffer.ClampBars(200));
        Assert.AreEqual(8, buffer.ClampBars(2));
        Assert.AreEqual(32, buffer.ClampBars(32));
    }

    [TestMethod]
    public void Feed_OverCapacityBars_KeepsSpectrumAtCapacity()
    {
        var buffer = new AudioFrameBuffer(barCount: 64);

        buffer.Feed(new float[512], bars: 200);

        AudioFrame frame = buffer.Snapshot();
        Assert.AreEqual(64, frame.Spectrum.Length, "The bar count must never exceed the fixed spectrum size");
    }

    [TestMethod]
    public void FeedAndSnapshot_FromDifferentThreads_NoException()
    {
        var buffer = new AudioFrameBuffer();
        var start = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        float[] block = Enumerable.Repeat(0.5f, 256).ToArray();

        Task feeder = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    buffer.Feed(block, 32);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        Task renderer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    AudioFrame frame = buffer.Snapshot();
                    foreach (float v in frame.Spectrum)
                    {
                        Assert.IsTrue(v is >= 0f and <= 1f);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        start.Set();
        Task.WaitAll(feeder, renderer);

        Assert.IsTrue(errors.IsEmpty, $"Concurrent feed/snapshot must not throw ({errors.Count} errors)");
    }
}
