using System.Diagnostics;
using ModernWigiDash.Sdk;
using NAudio.Wave;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("audio_visualizer", "Audio Visualizer", Description = "Visualizes live system audio output (Spotify, YouTube, Games) via WASAPI loopback capture.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Media & Audio", DefaultGridSize = GridSizePreset.Size4x2)]
public class AudioVisualizerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size5x2.ToSize();

    [WidgetProperty("Visualizer Style", WidgetPropertyType.Choice, "Bar spectrum or radial wave", "Neon Bars", "Neon Bars", "Oscilloscope Wave", "Radial Pulse")]
    public string VisualizerStyle { get; set; } = "Neon Bars";

    [WidgetProperty("Bar Count", WidgetPropertyType.Number, "Number of spectrum bars", 32f)]
    public float BarCount { get; set; } = 32f;

    [WidgetProperty("Primary Color", WidgetPropertyType.Color, "Color for high spectrum peaks", "#F59E0B")]
    public string PrimaryColorHex { get; set; } = "#F59E0B";

    private IAudioCaptureSource? _captureSource;
    private readonly AudioSpectrumAnalyzer _analyzer = new();
    private readonly Lock _audioLock = new();

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
    private long _lastRenderTimestamp = Stopwatch.GetTimestamp();

    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken);
        // Capture starts lazily on the first Render, not here.
    }

    private void EnsureLiveAudioCapture()
    {
        if (_capturing) return;

        StartLiveAudioCapture();
        _capturing = true;
    }

    private void StopLiveAudioCapture()
    {
        if (!_capturing) return;

        _capturing = false;
        IAudioCaptureSource? source = _captureSource;
        _captureSource = null;
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
        if (Stopwatch.GetElapsedTime(_lastRenderTimestamp).TotalSeconds > 1.0)
        {
            StopLiveAudioCapture();
            return;
        }

        lock (_audioLock)
        {
            _analyzer.Analyze(samples, (int)BarCount);
        }
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        // Capture runs only while this widget is being rendered (active page).
        // Prime the watchdog timestamp BEFORE capture starts, so the first
        // DataAvailable callback can never measure elapsed-since-epoch and
        // kill a fresh capture.
        _lastRenderTimestamp = Stopwatch.GetTimestamp();
        EnsureLiveAudioCapture();

        SKColor barColor = SKColor.TryParse(PrimaryColorHex, out var parsed) ? parsed : new SKColor(255, 205, 133);

        lock (_audioLock)
        {
            _analyzer.Smooth();
        }

        switch (VisualizerStyle)
        {
            case "Oscilloscope Wave":
                DrawOscilloscope(canvas, bounds, barColor);
                break;
            case "Radial Pulse":
                DrawRadialPulse(canvas, bounds, barColor);
                break;
            default:
                DrawNeonBars(canvas, bounds, barColor);
                break;
        }
    }

    private void DrawNeonBars(SKCanvas canvas, SKRect bounds, SKColor barColor)
    {
        int bars = (int)Math.Clamp(BarCount, 8, 64);
        float pad = 20f;
        float availableWidth = bounds.Width - (pad * 2);
        float barSpacing = 4f;
        float barWidth = (availableWidth - ((bars - 1) * barSpacing)) / bars;
        float maxBarHeight = bounds.Height - (pad * 2);

        lock (_audioLock)
        {
            ReadOnlySpan<float> spectrum = _analyzer.Spectrum;
            for (int i = 0; i < bars; i++)
            {
                float val = spectrum[i];
                float h = val * maxBarHeight;
                float x = pad + i * (barWidth + barSpacing);
                float y = bounds.Bottom - pad - h;

                var barBounds = new SKRect(x, y, x + barWidth, bounds.Bottom - pad);
                using var barPaint = new SKPaint
                {
                    Color = barColor,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(barBounds, 4f, 4f, barPaint);
            }
        }
    }

    private void DrawOscilloscope(SKCanvas canvas, SKRect bounds, SKColor color)
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

        lock (_audioLock)
        {
            var builder = new SKPathBuilder();
            float stepX = (bounds.Width - pad * 2f) / (_analyzer.WaveformLength - 1f);
            for (int i = 0; i < _analyzer.WaveformLength; i++)
            {
                float v = _analyzer.GetWaveform(i);
                float x = bounds.Left + pad + i * stepX;
                float y = midY - v * amp;
                if (i == 0)
                {
                    builder.MoveTo(x, y);
                }
                else
                {
                    builder.LineTo(x, y);
                }
            }
            canvas.DrawPath(builder.Detach(), linePaint);
        }
    }

    private void DrawRadialPulse(SKCanvas canvas, SKRect bounds, SKColor color)
    {
        float cx = bounds.MidX;
        float cy = bounds.MidY;
        float maxR = Math.Min(bounds.Width, bounds.Height) * 0.42f;
        int n = _analyzer.BarCount;

        using var linePaint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Clamp(bounds.Width / 64f, 2f, 5f),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };

        lock (_audioLock)
        {
            ReadOnlySpan<float> spectrum = _analyzer.Spectrum;
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
    }

    public override ValueTask DisposeAsync()
    {
        StopLiveAudioCapture();
        return base.DisposeAsync();
    }
}
