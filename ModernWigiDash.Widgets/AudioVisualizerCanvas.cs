namespace ModernWigiDash.Widgets;

/// <summary>
/// Encapsulates zero-allocation Skia rendering for the live audio visualizer.
/// Hoists all <see cref="SKPaint"/> instances, manages the reusable oscilloscope <see cref="SKPath"/>,
/// and implements neon spectrum bars, oscilloscope waveform, and radial pulse visualizations.
/// </summary>
internal sealed class AudioVisualizerCanvas : IDisposable
{
    private readonly SKPaint _barPaint = new() { IsAntialias = true };
    private readonly SKPaint _oscilloscopeLinePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
    private readonly SKPaint _radialLinePaint = new() { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private SKPath? _oscilloscopePath;

    /// <summary>
    /// Renders the visualizer frame in the specified style.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect bounds, AudioVisualizerMode mode, AudioFrame frame, int bars, SKColor barColor)
    {
        switch (mode)
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

        _barPaint.Color = barColor;

        for (int i = 0; i < bars; i++)
        {
            float val = spectrum[i];
            float h = val * maxBarHeight;
            float x = pad + i * (barWidth + barSpacing);
            float y = bounds.Bottom - pad - h;

            var barBounds = new SKRect(x, y, x + barWidth, bounds.Bottom - pad);
            canvas.DrawRoundRect(barBounds, 4f, 4f, _barPaint);
        }
    }

    private void DrawOscilloscope(SKCanvas canvas, SKRect bounds, SKColor color, ReadOnlySpan<float> waveform)
    {
        float pad = 16f;
        float midY = bounds.MidY;
        float amp = (bounds.Height - pad * 2f) * 0.45f;

        _oscilloscopeLinePaint.Color = color;

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
        canvas.DrawPath(_oscilloscopePath, _oscilloscopeLinePaint);
    }

    private void DrawRadialPulse(SKCanvas canvas, SKRect bounds, SKColor color, ReadOnlySpan<float> spectrum)
    {
        float cx = bounds.MidX;
        float cy = bounds.MidY;
        float maxR = Math.Min(bounds.Width, bounds.Height) * 0.42f;
        int n = spectrum.Length;

        _radialLinePaint.Color = color;
        _radialLinePaint.StrokeWidth = Math.Clamp(bounds.Width / 64f, 2f, 5f);

        for (int i = 0; i < n; i++)
        {
            float v = spectrum[i];
            float angle = (i / (float)n) * MathF.Tau;
            float dirX = MathF.Cos(angle);
            float dirY = MathF.Sin(angle);
            float len = 10f + v * maxR;
            canvas.DrawLine(cx + dirX * 6f, cy + dirY * 6f, cx + dirX * len, cy + dirY * len, _radialLinePaint);
        }
    }

    /// <summary>Disposes all hoisted Skia resources.</summary>
    public void Dispose()
    {
        _oscilloscopePath?.Dispose();
        _barPaint.Dispose();
        _oscilloscopeLinePaint.Dispose();
        _radialLinePaint.Dispose();
    }
}
