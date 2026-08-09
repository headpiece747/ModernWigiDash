using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// The visualizer's capture lifecycle driven through the
/// <see cref="IAudioCaptureSource"/> seam — no WASAPI hardware. Render starts
/// capture, sample blocks feed the analyzer, and the watchdog does not kill a
/// fresh capture (the first-callback race the old inline capture had).
/// </summary>
[TestClass]
public class AudioVisualizerWidgetTests
{
    private sealed class FakeCaptureSource : IAudioCaptureSource
    {
        public bool IsCapturing { get; private set; }
        public event Action<float[]>? SamplesAvailable;
        public List<float[]> Delivered { get; } = [];

        public void Start() => IsCapturing = true;

        public void Stop()
        {
            IsCapturing = false;
            SamplesAvailable = null;
        }

        public void Emit(float[] samples)
        {
            Delivered.Add(samples);
            SamplesAvailable?.Invoke(samples);
        }

        public void Dispose() => Stop();
    }

    private static SKCanvas CreateCanvas()
    {
        var surface = SKSurface.Create(new SKImageInfo(406, 296));
        return surface.Canvas;
    }

    [TestMethod]
    public async Task Render_StartsCaptureOnFirstRender()
    {
        var source = new FakeCaptureSource();
        var widget = new AudioVisualizerWidget { CaptureSourceFactory = () => source };
        await widget.InitializeAsync(new TestContext());

        widget.Render(CreateCanvas(), new SKRect(0, 0, 406, 296));

        Assert.IsTrue(source.IsCapturing, "The first Render must start capture");
        await widget.DisposeAsync();
        Assert.IsFalse(source.IsCapturing, "Dispose must stop capture");
    }

    [TestMethod]
    public async Task Render_WithSampleBlocks_DrawsWithoutException()
    {
        var source = new FakeCaptureSource();
        var widget = new AudioVisualizerWidget { CaptureSourceFactory = () => source };
        await widget.InitializeAsync(new TestContext());
        widget.Render(CreateCanvas(), new SKRect(0, 0, 406, 296));

        // Sample blocks arrive on the capture thread — here the test thread.
        source.Emit(Enumerable.Repeat(0.25f, 512).ToArray());
        source.Emit(Enumerable.Repeat(0.5f, 512).ToArray());

        // All three styles must render with the fed spectrum without throwing.
        foreach (string style in new[] { "Neon Bars", "Oscilloscope Wave", "Radial Pulse" })
        {
            widget.VisualizerStyle = style;
            widget.Render(CreateCanvas(), new SKRect(0, 0, 406, 296));
        }

        Assert.IsTrue(source.Delivered.Count == 2, "Both emitted blocks must reach the widget");
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async Task FreshCapture_IsNotKilledByWatchdog()
    {
        // The old inline capture started with _lastRenderTimestamp = 0, so the
        // first DataAvailable callback measured elapsed-since-epoch (> 1s) and
        // killed a brand-new capture. The timestamp is primed before start, so
        // an immediately-emitted block must be consumed, not stop capture.
        var source = new FakeCaptureSource();
        var widget = new AudioVisualizerWidget { CaptureSourceFactory = () => source };
        await widget.InitializeAsync(new TestContext());
        widget.Render(CreateCanvas(), new SKRect(0, 0, 406, 296));

        source.Emit(Enumerable.Repeat(0.25f, 512).ToArray());

        Assert.IsTrue(source.IsCapturing, "A fresh capture must not be killed by its first sample block");
        await widget.DisposeAsync();
    }

    [TestMethod]
    public void AudioVisualizerWidget_Render_AllStyles_ExecuteWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 406, 296);

        string[] styles = ["Neon Bars", "Oscilloscope Wave", "Radial Pulse"];
        foreach (var style in styles)
        {
            var widget = new AudioVisualizerWidget { VisualizerStyle = style };
            widget.Render(canvas, bounds);
        }

        Assert.IsNotNull(surface);
    }
}
