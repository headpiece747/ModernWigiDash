namespace ModernWigiDash.Widgets;

/// <summary>
/// Live audio visualizer: draws the system audio output's spectrum as neon
/// bars, an oscilloscope wave, or a radial pulse. The capture lifecycle and
/// the double-buffered DSP live in the AudioCaptureLifecycle and
/// AudioFrameBuffer modules; rendering is delegated to <see cref="AudioVisualizerCanvas"/>.
/// </summary>
[WidgetMetadata("audio_visualizer", "Audio Visualizer", Category = "Media & Audio", DefaultGridSize = GridSizePreset.Size5x2)]
public class AudioVisualizerWidget : ModernWidgetBase
{
    /// <summary>The "Visualizer Style" property: bar spectrum or radial wave.</summary>
    [WidgetProperty("Visualizer Style", WidgetPropertyType.Choice, "Bar spectrum or radial wave", "Neon Bars", "Neon Bars", "Oscilloscope Wave", "Radial Pulse")]
    public string VisualizerStyle { get; set; } = "Neon Bars";

    /// <summary>The "Bar Count" property: number of spectrum bars.</summary>
    [WidgetProperty("Bar Count", WidgetPropertyType.Number, "Number of spectrum bars", 32f)]
    public float BarCount { get; set; } = 32f;

    /// <summary>The "Primary Color" property: color for high spectrum peaks.</summary>
    [WidgetProperty("Primary Color", WidgetPropertyType.Color, "Color for high spectrum peaks", "#F59E0B")]
    public string PrimaryColorHex { get; set; } = "#F59E0B";

    private readonly AudioFrameBuffer _buffer = new();
    private AudioCaptureLifecycle? _lifecycle;
    private bool _disposed;
    private readonly AudioVisualizerCanvas _canvas = new();

    /// <summary>
    /// Test seam for the watchdog clock. Defaults to the system clock; tests
    /// inject a fake provider so the stale-render watchdog is drivable
    /// deterministically without sleeping.
    /// </summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// Test seam for the capture source. Defaults to the WASAPI loopback
    /// adapter; tests inject an in-memory source so the render/capture
    /// interplay and the DSP are drivable without audio hardware.
    /// </summary>
    internal Func<IAudioCaptureSource> CaptureSourceFactory { get; set; } = () => new WasapiLoopbackCaptureSource();

    /// <summary>
    /// Binds the context and builds the capture lifecycle (the start/stop
    /// policy and the NAudio marshaling) over the live source factory and
    /// clock seams.
    /// </summary>
    /// <param name="context">The widget host context.</param>
    /// <param name="cancellationToken">Cancels the initialization.</param>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _lifecycle = new AudioCaptureLifecycle(_buffer, () => CaptureSourceFactory(), () => Time, () => (int)BarCount, context.LogError);
    }

    /// <summary>
    /// Draws the selected visualizer style from the capture buffer's one
    /// locked snapshot; capture runs only while this widget renders (the
    /// lifecycle module owns the start/stop policy).
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _lifecycle?.OnRender();

        SKColor barColor = ColorOf(PrimaryColorHex, WidgetPalette.Accent);
        AudioFrame frame = _buffer.Snapshot();
        int bars = _buffer.ClampBars((int)BarCount);
        AudioVisualizerMode mode = AudioVisualizerModeParser.Parse(VisualizerStyle);

        _canvas.Render(canvas, bounds, mode, frame, bars, barColor);
    }

    /// <summary>Stops the capture lifecycle and disposes the canvas and Skia resources.</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _lifecycle?.Dispose();
        _canvas.Dispose();
        return base.DisposeAsync();
    }
}
