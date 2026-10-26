namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The audio visualizer canvas's draw surface (the house render-smoke
/// pattern, CozyTodoCanvasTests): all three visualizer styles paint onto a
/// real canvas from a real frame (the AudioFrameBuffer's snapshot shape),
/// so a regression that drops a draw path is caught where the pixels land.
/// </summary>
[TestClass]
public class AudioVisualizerCanvasTests
{
    private static AudioFrame Frame()
    {
        var spectrum = new float[64];
        for (int i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] = (float)(i / (double)spectrum.Length);
        }

        var waveform = new float[128];
        for (int i = 0; i < waveform.Length; i++)
        {
            waveform[i] = MathF.Sin(i / 8f);
        }

        return new AudioFrame(spectrum, waveform);
    }

    [TestMethod]
    public void Render_NeonBars_Smoke_PaintsTheBars()
    {
        using var canvas = new AudioVisualizerCanvas();
        using var bitmap = new SKBitmap(400, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.Render(skCanvas, new SKRect(0, 0, 400, 200), AudioVisualizerMode.NeonBars, Frame(), 32, SKColors.Cyan);

        Assert.IsTrue(Painted(bitmap), "the neon-bars visualizer must paint onto the canvas");
    }

    [TestMethod]
    public void Render_Oscilloscope_Smoke_PaintsTheTrace()
    {
        using var canvas = new AudioVisualizerCanvas();
        using var bitmap = new SKBitmap(400, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.Render(skCanvas, new SKRect(0, 0, 400, 200), AudioVisualizerMode.Oscilloscope, Frame(), 32, SKColors.Cyan);

        Assert.IsTrue(Painted(bitmap), "the oscilloscope trace must paint onto the canvas");
    }

    [TestMethod]
    public void Render_RadialPulse_Smoke_PaintsThePulse()
    {
        using var canvas = new AudioVisualizerCanvas();
        using var bitmap = new SKBitmap(400, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.Render(skCanvas, new SKRect(0, 0, 400, 200), AudioVisualizerMode.RadialPulse, Frame(), 32, SKColors.Cyan);

        Assert.IsTrue(Painted(bitmap), "the radial-pulse visualizer must paint onto the canvas");
    }

    private static bool Painted(SKBitmap bitmap)
    {
        for (int x = 20; x < bitmap.Width - 20; x += 5)
        {
            for (int y = 20; y < bitmap.Height - 20; y += 5)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
