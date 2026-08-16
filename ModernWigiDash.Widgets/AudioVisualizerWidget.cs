using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("audio_visualizer", "Audio Visualizer", Category = "Media & Audio")]
public class AudioVisualizerWidget : ModernWidgetBase
{
    public override SKSize DefaultSize => GridSizePreset.Size5x2.ToSize();

    [WidgetProperty("Visualizer Style", WidgetPropertyType.Choice, "Bar spectrum or radial wave", "Neon Bars", "Neon Bars", "Oscilloscope Wave", "Radial Pulse")]
    public string VisualizerStyle { get; set; } = "Neon Bars";

    [WidgetProperty("Bar Count", WidgetPropertyType.Number, "Number of spectrum bars", 32f)]
    public float BarCount { get; set; } = 32f;

    [WidgetProperty("Primary Color", WidgetPropertyType.Color, "Color for high spectrum peaks", "#F59E0B")]
    public string PrimaryColorHex { get; set; } = "#F59E0B";

    private IAudioCaptureSource? _captureSource;
    private readonly AudioFrameBuffer _buffer = new();
    // Capture lifecycle is touched from the UI render thread (start), the
    // capture thread (the watchdog stop), and the thread pool (the deferred
    // NAudio dispose) — one lock serializes the start/stop sequences so a
    // watchdog firing as the page switches back can never unsubscribe/dispose
    // a source mid-start (a lost race self-heals on the next render tick,
    // which re-arms capture).
    private readonly Lock _captureLock = new();

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

    // Capture is tied to rendering: it starts on the first Render (i.e. when
    // the widget's page becomes active) and stops when Render stops being
    // called for a grace period (page switched away). WASAPI loopback capture
    // would otherwise run forever in the background for a hidden widget.
    private volatile bool _capturing;
    private volatile bool _stopQueued;
    private long _lastRenderTimestamp = TimeProvider.System.GetTimestamp();

    public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        return base.InitializeAsync(context, cancellationToken);
        // Capture starts lazily on the first Render, not here.
    }

    private void EnsureLiveAudioCapture()
    {
        if (_capturing) return;

        lock (_captureLock)
        {
            if (_capturing) return;
            StartLiveAudioCapture();
            _capturing = true;
        }
    }

    private void StopLiveAudioCapture()
    {
        if (!_capturing) return;

        lock (_captureLock)
        {
            _stopQueued = false;
            IAudioCaptureSource? source = _captureSource;
            _captureSource = null;
            _capturing = false;
            try
            {
                if (source != null) source.SamplesAvailable -= OnSamplesAvailable;
                source?.Dispose();
            }
            catch (Exception ex)
            {
                Context?.LogError("Failed to stop audio capture", ex);
            }
        }
    }

    private void StartLiveAudioCapture()
    {
        try
        {
            IAudioCaptureSource source = CaptureSourceFactory();
            source.SamplesAvailable += OnSamplesAvailable;
            source.Start();
            _captureSource = source;
        }
        catch (Exception ex)
        {
            _captureSource?.Dispose();
            _captureSource = null;
            Context?.LogError("Failed to initialize audio capture", ex);
        }
    }

    private void OnSamplesAvailable(float[] samples)
    {
        // Watchdog: when the widget is no longer rendered (page switched
        // away), stop capture instead of running forever. _lastRenderTimestamp
        // is primed before capture starts, so the first callback cannot kill a
        // fresh capture (the old code left it at 0 — elapsed-since-epoch
        // always exceeded the grace period).
        //
        // NAudio raises DataAvailable from inside ReadNextPacket on the capture
        // thread, and WasapiCapture.Dispose joins that same thread. Stopping
        // synchronously here would self-join and deadlock the capture thread
        // while it holds the capture lock, blocking the UI render thread on
        // that lock forever. The stop is therefore deferred to the thread pool
        // and the lock still serializes it against a concurrent start.
        if (Time.GetElapsedTime(_lastRenderTimestamp).TotalSeconds > 1.0)
        {
            // Queue at most one deferred stop; the first work item that runs
            // resets the flag (under the lock) before disposing.
            if (!_stopQueued)
            {
                _stopQueued = true;
                ThreadPool.QueueUserWorkItem(_ => StopLiveAudioCapture());
            }
            return;
        }

        _buffer.Feed(samples, _buffer.ClampBars((int)BarCount));
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        // Capture runs only while this widget is being rendered (active page).
        // Prime the watchdog timestamp BEFORE capture starts, so the first
        // DataAvailable callback can never measure elapsed-since-epoch and
        // kill a fresh capture.
        _lastRenderTimestamp = Time.GetTimestamp();
        EnsureLiveAudioCapture();

        SKColor barColor = ColorOf(PrimaryColorHex, WidgetPalette.Accent);

        // One locked copy per frame (smooth + double-buffered snapshot); the
        // draw methods below read the copy without holding the gate.
        AudioFrame frame = _buffer.Snapshot();
        int bars = _buffer.ClampBars((int)BarCount);

        switch (AudioVisualizerModeParser.Parse(VisualizerStyle))
        {
            case AudioVisualizerMode.Oscilloscope:
                DrawOscilloscope(canvas, bounds, barColor, frame.Waveform);
                break;
            case AudioVisualizerMode.RadialPulse:
                DrawRadialPulse(canvas, bounds, barColor, frame.Spectrum);
                break;
            default:
                DrawNeonBars(canvas, bounds, barColor, frame.Spectrum, bars);
                break;
        }
    }

    private void DrawNeonBars(SKCanvas canvas, SKRect bounds, SKColor barColor, ReadOnlySpan<float> spectrum, int bars)
    {
        float pad = 20f;
        float availableWidth = bounds.Width - (pad * 2);
        float barSpacing = 4f;
        float barWidth = (availableWidth - ((bars - 1) * barSpacing)) / bars;
        float maxBarHeight = bounds.Height - (pad * 2);

        // One paint shared by every bar (the old code allocated an SKPaint per
        // bar per frame).
        using var barPaint = new SKPaint
        {
            Color = barColor,
            IsAntialias = true
        };

        for (int i = 0; i < bars; i++)
        {
            float val = spectrum[i];
            float h = val * maxBarHeight;
            float x = pad + i * (barWidth + barSpacing);
            float y = bounds.Bottom - pad - h;

            var barBounds = new SKRect(x, y, x + barWidth, bounds.Bottom - pad);
            canvas.DrawRoundRect(barBounds, 4f, 4f, barPaint);
        }
    }

    private void DrawOscilloscope(SKCanvas canvas, SKRect bounds, SKColor color, ReadOnlySpan<float> waveform)
    {
        float pad = 16f;
        float midY = bounds.MidY;
        float amp = (bounds.Height - pad * 2f) * 0.45f;

        using var linePaint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        // The wave shape changes every frame, so the SKPath object is reused
        // and re-lined instead of building + detaching a new one (SKPathBuilder
        // has no reuse path — every Snapshot/Detach allocates a new SKPath).
        _oscilloscopePath ??= new SKPath();
#pragma warning disable CS0618 // SKPath.Rewind/MoveTo/LineTo are obsolete in favor of SKPathBuilder, whose Snapshot()/Detach() allocate a new SKPath per call — the wave path is reused and re-lined instead (zero-alloc hot path).
        _oscilloscopePath.Rewind();
        float stepX = (bounds.Width - pad * 2f) / (waveform.Length - 1f);
        for (int i = 0; i < waveform.Length; i++)
        {
            float v = waveform[i];
            float x = bounds.Left + pad + i * stepX;
            float y = midY - v * amp;
            if (i == 0)
            {
                _oscilloscopePath.MoveTo(x, y);
            }
            else
            {
                _oscilloscopePath.LineTo(x, y);
            }
        }
#pragma warning restore CS0618
        canvas.DrawPath(_oscilloscopePath, linePaint);
    }

    private void DrawRadialPulse(SKCanvas canvas, SKRect bounds, SKColor color, ReadOnlySpan<float> spectrum)
    {
        float cx = bounds.MidX;
        float cy = bounds.MidY;
        float maxR = Math.Min(bounds.Width, bounds.Height) * 0.42f;
        int n = spectrum.Length;

        using var linePaint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Clamp(bounds.Width / 64f, 2f, 5f),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };

        for (int i = 0; i < n; i++)
        {
            float v = spectrum[i];
            float angle = (i / (float)n) * MathF.Tau;
            float dirX = MathF.Cos(angle);
            float dirY = MathF.Sin(angle);
            float len = 10f + v * maxR;
            canvas.DrawLine(cx + dirX * 6f, cy + dirY * 6f, cx + dirX * len, cy + dirY * len, linePaint);
        }
    }

    public override ValueTask DisposeAsync()
    {
        StopLiveAudioCapture();
        _oscilloscopePath?.Dispose();
        return base.DisposeAsync();
    }

    // The reused oscilloscope wave path (re-lined per frame, never reallocated).
    private SKPath? _oscilloscopePath;
}
