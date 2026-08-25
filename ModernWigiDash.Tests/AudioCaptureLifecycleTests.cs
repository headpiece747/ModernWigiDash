namespace ModernWigiDash.Tests;

/// <summary>
/// The visualizer's capture-lifecycle module driven directly through the
/// <see cref="IAudioCaptureSource"/> seam (and a fake clock) — the
/// start-on-render / stop-on-stale policy, the watchdog, the deferred-stop
/// marshaling, and the start-failure retry, assertable without a widget, a
/// canvas, or audio hardware. The widget's integration path (Render drives
/// the module) is pinned in AudioVisualizerWidgetTests.
/// </summary>
[TestClass]
public class AudioCaptureLifecycleTests
{
    [TestMethod]
    public void OnRender_StartsCaptureOnFirstRender_AndStopReleasesIt()
    {
        var source = new FakeAudioCaptureSource();
        var clock = new FakeTimeProvider();
        var lifecycle = new AudioCaptureLifecycle(new AudioFrameBuffer(), () => source, () => clock, () => 32, (_, _) => { });

        lifecycle.OnRender();

        Assert.IsTrue(source.IsCapturing, "the first render tick must start capture");

        lifecycle.Stop();

        Assert.IsFalse(source.IsCapturing, "Stop must release the source");
        Assert.AreEqual(1, source.DisposalCount, "releasing disposes the source exactly once");
    }

    [TestMethod]
    public void FreshCapture_IsNotKilledByWatchdog()
    {
        // The timestamp is primed before capture starts, so the first sample
        // block must be consumed, not stop the capture.
        var source = new FakeAudioCaptureSource();
        var clock = new FakeTimeProvider();
        var lifecycle = new AudioCaptureLifecycle(new AudioFrameBuffer(), () => source, () => clock, () => 32, (_, _) => { });

        lifecycle.OnRender();
        source.Emit(Enumerable.Repeat(0.25f, 512).ToArray());

        Assert.IsTrue(source.IsCapturing, "a fresh capture must not be killed by its first sample block");
        lifecycle.Stop();
    }

    [TestMethod]
    public async Task StaleCapture_AfterRenderGap_WatchdogStopsIt()
    {
        // After the 1s grace period elapses, the next sample block stops
        // capture — marshaled off the capture thread, so it lands
        // asynchronously: poll for it.
        var source = new FakeAudioCaptureSource();
        var clock = new FakeTimeProvider();
        var lifecycle = new AudioCaptureLifecycle(new AudioFrameBuffer(), () => source, () => clock, () => 32, (_, _) => { });

        lifecycle.OnRender();
        Assert.IsTrue(source.IsCapturing, "capture must start on the first render tick");

        clock.Advance(TimeSpan.FromSeconds(2));
        source.Emit(Enumerable.Repeat(0.25f, 512).ToArray());

        await TestWait.WaitUntilAsync(() => !source.IsCapturing, TimeSpan.FromSeconds(5));
        Assert.IsFalse(source.IsCapturing, "a stale capture must be stopped by the watchdog");
    }

    [TestMethod]
    public async Task StaleCapture_ThenRenderAgain_ReArmsCapture()
    {
        // Page-switch-back: a stale capture stops, and the next render tick
        // starts capture again on a fresh source.
        var firstSource = new FakeAudioCaptureSource();
        var secondSource = new FakeAudioCaptureSource();
        var sources = new Queue<FakeAudioCaptureSource>([firstSource, secondSource]);
        var clock = new FakeTimeProvider();
        var lifecycle = new AudioCaptureLifecycle(new AudioFrameBuffer(), () => sources.Dequeue(), () => clock, () => 32, (_, _) => { });

        lifecycle.OnRender();
        Assert.IsTrue(firstSource.IsCapturing);

        clock.Advance(TimeSpan.FromSeconds(2));
        firstSource.Emit(Enumerable.Repeat(0.25f, 512).ToArray());
        await TestWait.WaitUntilAsync(() => !firstSource.IsCapturing, TimeSpan.FromSeconds(5));

        lifecycle.OnRender();

        Assert.IsTrue(secondSource.IsCapturing, "re-rendering must start fresh capture");
        lifecycle.Stop();
    }

    [TestMethod]
    public void SampleBlocks_FeedTheBuffer()
    {
        var buffer = new AudioFrameBuffer(32);
        var source = new FakeAudioCaptureSource();
        var clock = new FakeTimeProvider();
        var lifecycle = new AudioCaptureLifecycle(buffer, () => source, () => clock, () => 32, (_, _) => { });

        lifecycle.OnRender();
        source.Emit(Enumerable.Repeat(0.5f, 512).ToArray());

        var frame = buffer.Snapshot();

        Assert.IsTrue(frame.Waveform.Any(v => v > 0f), "a fed block must reach the waveform ring");
        lifecycle.Stop();
    }

    [TestMethod]
    public void StartFailure_DisposesTheHalfOpenedSource_LogsAndRetriesOnTheNextRender()
    {
        // A failed start (the device side throws from Start) must dispose the
        // half-opened source, log once, and stay stopped — and the next
        // render tick must retry the start (the old inline version marked the
        // widget as capturing on the swallowed failure and never retried).
        var halfOpen = new FakeAudioCaptureSource { FailStart = true };
        var goodSource = new FakeAudioCaptureSource();
        List<string> errors = [];
        int attempts = 0;
        var clock = new FakeTimeProvider();
        Func<IAudioCaptureSource> factory = () =>
        {
            attempts++;
            return attempts == 1 ? halfOpen : goodSource;
        };
        var lifecycle = new AudioCaptureLifecycle(new AudioFrameBuffer(), factory, () => clock, () => 32, (message, _) => errors.Add(message));

        lifecycle.OnRender();

        Assert.IsFalse(goodSource.IsCapturing, "a failed start must not capture");
        Assert.AreEqual(1, halfOpen.DisposalCount, "a half-opened source from a failed start must be disposed");
        Assert.AreEqual(1, errors.Count, "the failure must log once");

        lifecycle.OnRender();

        Assert.AreEqual(2, attempts, "the second render tick must retry the start");
        Assert.IsTrue(goodSource.IsCapturing, "a retried start on a live device must capture");
        lifecycle.Stop();
    }
}
