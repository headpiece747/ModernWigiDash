using System.Globalization;
using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("hardware_monitor", "Hardware Monitor", Description = "Show live hardware telemetry (temperature, load, fan, etc.) as a gauge, bar, or sparkline graph. Data is read from LibreHardwareService's shared-memory maps, so the ModernWigiDash service is not involved.", Author = "ModernWigiDash", Version = "2.1.0", Category = "System Monitoring", DefaultGridSize = GridSizePreset.Size2x2)]
public class HardwareMonitorWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector, "Select a live sensor reading from LibreHardwareService", "")]
    public string SensorLabel { get; set; } = "";

    [WidgetProperty("Display Label", WidgetPropertyType.Text, "Override the label shown on the widget (leave empty to use the sensor name)", "")]
    public string DisplayLabel { get; set; } = "";

    [WidgetProperty("Unit", WidgetPropertyType.Text, "Override the unit (leave empty to use the sensor's)", "")]
    public string Unit { get; set; } = "";

    [WidgetProperty("Display Mode", WidgetPropertyType.Choice, "How to visualize the reading", "Gauge", "Gauge", "Bar", "Value", "Graph")]
    public string DisplayMode { get; set; } = "Gauge";

    [WidgetProperty("Auto Scale", WidgetPropertyType.Boolean, "Scale the gauge/bar to the maximum recorded by the sensor", true)]
    public bool AutoScale { get; set; } = true;

    [WidgetProperty("Max Value", WidgetPropertyType.Number, "Manual gauge/bar maximum when Auto Scale is off", 100f)]
    public float MaxValue { get; set; } = 100f;

    [WidgetProperty("Decimals", WidgetPropertyType.Number, "Number of decimal places shown", 1f)]
    public float Decimals { get; set; } = 1f;

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private readonly Queue<float> _history = new();
    private const int HistoryCapacity = 96;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // The store owns the staleness decision; a stale or disconnected
        // snapshot renders the unavailable state instead of frozen data.
        LhmSnapshot? snapshot = LhmSensorStore.TryReadFresh();
        if (snapshot == null || !snapshot.IsConnected)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "No sensor data", "Start LibreHardwareService to read hardware sensors", text);
            return;
        }

        if (string.IsNullOrWhiteSpace(SensorLabel))
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Select a sensor", "Open Settings and pick a sensor reading", text);
            return;
        }

        LhmReading? reading = snapshot.Readings.FirstOrDefault(r => string.Equals(r.Label, SensorLabel, StringComparison.OrdinalIgnoreCase));
        if (reading == null)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Sensor not found", $"{SensorLabel} is not currently available", text);
            return;
        }

        float value = (float)reading.Value;
        float max = ResolveMax(reading, value);
        int decimals = Math.Clamp((int)MathF.Round(Decimals), 0, 3);

        string label = string.IsNullOrWhiteSpace(DisplayLabel) ? reading.Label : DisplayLabel;
        string unit = string.IsNullOrWhiteSpace(Unit) ? reading.Unit : Unit;

        _history.Enqueue(value);
        while (_history.Count > HistoryCapacity)
        {
            _history.Dequeue();
        }

        switch (DisplayMode)
        {
            case "Bar":
                RenderBar(canvas, bounds, label, value, max, unit, decimals, accent, text);
                break;
            case "Value":
                RenderValue(canvas, bounds, label, value, unit, decimals, text);
                break;
            case "Graph":
                RenderGraph(canvas, bounds, label, value, unit, decimals, accent, text, reading);
                break;
            default:
                RenderGauge(canvas, bounds, label, value, max, unit, decimals, accent, text);
                break;
        }
    }

    private float ResolveMax(LhmReading reading, float value)
    {
        if (AutoScale)
        {
            double reference = Math.Max(reading.Max, reading.Avg);
            reference = Math.Max(reference, value);
            return reference > 0 ? (float)reference : Math.Max(1f, value * 1.2f);
        }

        return MaxValue > 0 ? MaxValue : Math.Max(1f, value * 1.2f);
    }

    private static void DrawHeader(SKCanvas canvas, SKRect bounds, string label, float pad, SKColor text)
    {
        var headerFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, headerFont, headerPaint);
    }

    private void RenderGauge(SKCanvas canvas, SKRect bounds, string label, float value, float max, string unit, int decimals, SKColor accent, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        float gaugeSize = Math.Min(bounds.Width * 0.42f, bounds.Height - 48f);
        float cx = bounds.MidX;
        float cy = bounds.MidY + 10f;
        float radius = (gaugeSize / 2f) - 10f;
        var arcBounds = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        using var trackPaint = new SKPaint { Color = text.WithAlpha(20), Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        var trackPath = new SKPathBuilder();
        trackPath.AddArc(arcBounds, 135f, 270f);
        canvas.DrawPath(trackPath.Snapshot(), trackPaint);

        float progress = Math.Clamp(value / Math.Max(1f, max), 0f, 1f);
        using var progressPaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        var progressPath = new SKPathBuilder();
        progressPath.AddArc(arcBounds, 135f, 270f * progress);
        canvas.DrawPath(progressPath.Snapshot(), progressPaint);

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, gaugeSize * 0.2f);
        using var valPaint = new SKPaint { Color = text, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawTextWithFallback(valStr, cx - valBounds.Width / 2f, cy + valBounds.Height / 3f, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawTextWithFallback(unit, cx - valBounds.Width / 2f + valBounds.Width + 4f, cy + valBounds.Height / 3f, unitFont, unitPaint);
        }
    }

    private void RenderBar(SKCanvas canvas, SKRect bounds, string label, float value, float max, string unit, int decimals, SKColor accent, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(bounds.Height * 0.22f, 22f, 48f));
        using var valPaint = new SKPaint { Color = text, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawTextWithFallback(valStr, bounds.MidX - valBounds.Width / 2f, bounds.MidY + 4f, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 12f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawTextWithFallback(unit, bounds.MidX - valBounds.Width / 2f + valBounds.Width + 5f, bounds.MidY + 4f, unitFont, unitPaint);
        }

        var barRect = new SKRect(bounds.Left + pad, bounds.MidY + 20f, bounds.Right - pad, bounds.MidY + 32f);
        float trackRadius = barRect.Height / 2f;

        using var trackPaint = new SKPaint { Color = text.WithAlpha(20), IsAntialias = true };
        canvas.DrawRoundRect(barRect, trackRadius, trackRadius, trackPaint);

        float progress = Math.Clamp(value / Math.Max(1f, max), 0f, 1f);
        float progressWidth = Math.Max(barRect.Height, barRect.Width * progress);
        var progressRect = new SKRect(barRect.Left, barRect.Top, barRect.Left + progressWidth, barRect.Bottom);
        float progressRadius = Math.Min(trackRadius, progressWidth / 2f);
        using var progressPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawRoundRect(progressRect, progressRadius, progressRadius, progressPaint);
    }

    private void RenderValue(SKCanvas canvas, SKRect bounds, string label, float value, string unit, int decimals, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        float valFontSize = Math.Min(bounds.Width * 0.22f, bounds.Height * 0.42f);
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, valFontSize);
        using var valPaint = new SKPaint { Color = text, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawTextWithFallback(valStr, bounds.MidX - valBounds.Width / 2f, bounds.MidY + 4f, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawTextWithFallback(unit, bounds.MidX - valBounds.Width / 2f + valBounds.Width + 6f, bounds.MidY + 4f, unitFont, unitPaint);
        }
    }

    private void RenderGraph(SKCanvas canvas, SKRect bounds, string label, float value, string unit, int decimals, SKColor accent, SKColor text, LhmReading reading)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        float graphTop = bounds.Top + 40f;
        float graphBottom = bounds.Bottom - pad;
        var area = new SKRect(bounds.Left + pad, graphTop, bounds.Right - pad, graphBottom);

        int count = _history.Count;
        if (count >= 2)
        {
            // Zero-alloc: copy the float history onto the stack for the
            // sparkline, computing min/max in the same single pass (replaces
            // Cast<double>().ToList() + Min() + Max() per frame).
            Span<float> samples = count <= HistoryCapacity
                ? stackalloc float[count]
                : new float[count];
            float min = float.MaxValue;
            float max = float.MinValue;
            int i = 0;
            foreach (float sample in _history)
            {
                samples[i++] = sample;
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }

            float lo = Math.Min(min, (float)reading.Min);
            float hi = Math.Max(max, (float)reading.Max);
            if (hi - lo < 1e-6f)
            {
                lo = value - 1f;
                hi = value + 1f;
            }

            TextRenderHelper.DrawSparkline(canvas, area, samples, lo, hi, accent);
        }

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 22f);
        using var valPaint = new SKPaint { Color = accent, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawTextWithFallback(valStr, area.Right - valBounds.Width, area.Top + valFont.Size, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawTextWithFallback(unit, area.Right - valBounds.Width + valBounds.Width + 4f, area.Top + valFont.Size, unitFont, unitPaint);
        }
    }
}

