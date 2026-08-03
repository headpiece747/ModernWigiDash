using System.Globalization;
using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("hardware_monitor", "Hardware Monitor", "Show live hardware telemetry (temperature, load, fan, etc.) as a gauge, bar, or sparkline graph. Data is collected by the ModernWigiDash service via LibreHardwareMonitor, so no separate monitoring app is required.", "ModernWigiDash", "2.1.0", "System Monitoring", GridSizePreset.Size2x2)]
public class HardwareMonitorWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector, "Select a live sensor reading from the service", "")]
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
        SKColor accent = SKColor.TryParse(AccentColorHex, out var parsed) ? parsed : new SKColor(255, 205, 133);
        SKColor text = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;

        LhmSnapshot snapshot = LhmSensorStore.ReadSnapshot();
        if (!snapshot.IsConnected)
        {
            DrawPlaceholder(canvas, bounds, "No sensor data", "Start the ModernWigiDash service to read hardware sensors", text);
            return;
        }

        if (string.IsNullOrWhiteSpace(SensorLabel))
        {
            DrawPlaceholder(canvas, bounds, "Select a sensor", "Open Settings and pick a sensor reading", text);
            return;
        }

        LhmReading? reading = snapshot.Readings.FirstOrDefault(r => string.Equals(r.Label, SensorLabel, StringComparison.OrdinalIgnoreCase));
        if (reading == null)
        {
            DrawPlaceholder(canvas, bounds, "Sensor not found", $"{SensorLabel} is not currently available", text);
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
                RenderBar(canvas, bounds, label, value, max, unit, decimals, accent, text, reading);
                break;
            case "Value":
                RenderValue(canvas, bounds, label, value, unit, decimals, text, reading);
                break;
            case "Graph":
                RenderGraph(canvas, bounds, label, value, unit, decimals, accent, text, reading);
                break;
            default:
                RenderGauge(canvas, bounds, label, value, max, unit, decimals, accent, text, reading);
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

    private static void DrawPlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text)
    {
        float pad = 16f;
        using var titleFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 18f);
        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        titleFont.MeasureText(title, out var titleBounds, titlePaint);
        canvas.DrawText(title, bounds.MidX - titleBounds.Width / 2f, bounds.MidY - 2f, SKTextAlign.Left, titleFont, titlePaint);

        using var subFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, 11f);
        using var subPaint = new SKPaint { Color = text.WithAlpha(150), IsAntialias = true };
        subFont.MeasureText(subtitle, out var subBounds, subPaint);
        canvas.DrawText(subtitle, bounds.MidX - subBounds.Width / 2f, bounds.MidY + 20f, SKTextAlign.Left, subFont, subPaint);
    }

    private void RenderGauge(SKCanvas canvas, SKRect bounds, string label, float value, float max, string unit, int decimals, SKColor accent, SKColor text, LhmReading reading)
    {
        float pad = 16f;
        using var headerFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawText(TruncateHeader(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, SKTextAlign.Left, headerFont, headerPaint);

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
        using var valFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, gaugeSize * 0.2f);
        using var valPaint = new SKPaint { Color = text, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawText(valStr, cx - valBounds.Width / 2f, cy + valBounds.Height / 3f, SKTextAlign.Left, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            using var unitFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 11f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawText(unit, cx - valBounds.Width / 2f + valBounds.Width + 4f, cy + valBounds.Height / 3f, SKTextAlign.Left, unitFont, unitPaint);
        }
    }

    private void RenderBar(SKCanvas canvas, SKRect bounds, string label, float value, float max, string unit, int decimals, SKColor accent, SKColor text, LhmReading reading)
    {
        float pad = 16f;
        using var headerFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawText(TruncateHeader(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, SKTextAlign.Left, headerFont, headerPaint);

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        using var valFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Clamp(bounds.Height * 0.22f, 22f, 48f));
        using var valPaint = new SKPaint { Color = text, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawText(valStr, bounds.MidX - valBounds.Width / 2f, bounds.MidY + 4f, SKTextAlign.Left, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            using var unitFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 12f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawText(unit, bounds.MidX - valBounds.Width / 2f + valBounds.Width + 5f, bounds.MidY + 4f, SKTextAlign.Left, unitFont, unitPaint);
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

    private void RenderValue(SKCanvas canvas, SKRect bounds, string label, float value, string unit, int decimals, SKColor text, LhmReading reading)
    {
        float pad = 16f;
        using var headerFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawText(TruncateHeader(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, SKTextAlign.Left, headerFont, headerPaint);

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        float valFontSize = Math.Min(bounds.Width * 0.22f, bounds.Height * 0.42f);
        using var valFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, valFontSize);
        using var valPaint = new SKPaint { Color = text, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawText(valStr, bounds.MidX - valBounds.Width / 2f, bounds.MidY + 4f, SKTextAlign.Left, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            using var unitFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 14f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawText(unit, bounds.MidX - valBounds.Width / 2f + valBounds.Width + 6f, bounds.MidY + 4f, SKTextAlign.Left, unitFont, unitPaint);
        }
    }

    private void RenderGraph(SKCanvas canvas, SKRect bounds, string label, float value, string unit, int decimals, SKColor accent, SKColor text, LhmReading reading)
    {
        float pad = 16f;
        using var headerFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawText(TruncateHeader(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, SKTextAlign.Left, headerFont, headerPaint);

        float graphTop = bounds.Top + 40f;
        float graphBottom = bounds.Bottom - pad;
        var area = new SKRect(bounds.Left + pad, graphTop, bounds.Right - pad, graphBottom);

        if (_history.Count >= 2)
        {
            float lo = Math.Min(_history.Min(), (float)reading.Min);
            float hi = Math.Max(_history.Max(), (float)reading.Max);
            if (hi - lo < 1e-6f)
            {
                lo = value - 1f;
                hi = value + 1f;
            }

            float span = area.Width / (_history.Count - 1);
            var line = new SKPath();
            var fill = new SKPath();
            bool first = true;
            int index = 0;
            foreach (float sample in _history)
            {
                float x = area.Left + index * span;
                float y = area.Bottom - (sample - lo) / (hi - lo) * area.Height;
                if (first)
                {
                    line.MoveTo(x, y);
                    fill.MoveTo(x, y);
                    first = false;
                }
                else
                {
                    line.LineTo(x, y);
                    fill.LineTo(x, y);
                }
                index++;
            }

            fill.LineTo(area.Right, area.Bottom);
            fill.LineTo(area.Left, area.Bottom);
            fill.Close();
            using var fillPaint = new SKPaint { Color = accent.WithAlpha(45), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawPath(fill, fillPaint);

            using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
            canvas.DrawPath(line, linePaint);
        }

        string valStr = value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
        using var valFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 22f);
        using var valPaint = new SKPaint { Color = accent, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);
        canvas.DrawText(valStr, area.Right - valBounds.Width, area.Top + valFont.Size, SKTextAlign.Left, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            using var unitFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 11f);
            using var unitPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            canvas.DrawText(unit, area.Right - valBounds.Width + valBounds.Width + 4f, area.Top + valFont.Size, SKTextAlign.Left, unitFont, unitPaint);
        }
    }

    private static string TruncateHeader(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        string trimmed = text;
        while (trimmed.Length > 1 && font.MeasureText(trimmed + "…") > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed + "…";
    }

    public override ValueTask DisposeAsync()
    {
        return base.DisposeAsync();
    }
}

public static class TelemetryRenderer
{
    public static void RenderGaugeCard(SKCanvas canvas, SKRect bounds, string header, string label, float value, float maxValue, string unit, SKColor accentColor, SKColor textColor)
    {
        float pad = 16f;
        using var headerFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 11f);
        using var headerPaint = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawText(header, pad, pad + 11f, SKTextAlign.Left, headerFont, headerPaint);

        using var labelFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 18f);
        using var labelPaint = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawText(label, pad, pad + 35f, SKTextAlign.Left, labelFont, labelPaint);

        float gaugeSize = Math.Min(bounds.Width * 0.45f, bounds.Height - 50f);
        float cx = bounds.Right - pad - (gaugeSize / 2f);
        float cy = bounds.MidY + 10f;
        float radius = (gaugeSize / 2f) - 10f;

        using var trackPaint = new SKPaint { Color = textColor.WithAlpha(20), Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        var arcBounds = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        var pathTrackBuilder = new SKPathBuilder();
        pathTrackBuilder.AddArc(arcBounds, 135f, 270f);
        using var pathTrack = pathTrackBuilder.Snapshot();
        canvas.DrawPath(pathTrack, trackPaint);

        float progress = Math.Clamp(value / Math.Max(1f, maxValue), 0f, 1f);
        using var progressPaint = new SKPaint { Color = accentColor, Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        var pathProgressBuilder = new SKPathBuilder();
        pathProgressBuilder.AddArc(arcBounds, 135f, 270f * progress);
        using var pathProgress = pathProgressBuilder.Snapshot();
        canvas.DrawPath(pathProgress, progressPaint);

        string valStr = $"{value:F1}";
        using var valFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, gaugeSize * 0.22f);
        using var valPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var valBounds = new SKRect();
        valFont.MeasureText(valStr, out valBounds, valPaint);
        canvas.DrawText(valStr, cx - (valBounds.Width / 2f), cy + (valBounds.Height / 3f), SKTextAlign.Left, valFont, valPaint);

        using var unitFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 12f);
        using var unitPaint = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawText(unit, cx - (valBounds.Width / 2f) + valBounds.Width + 4f, cy + (valBounds.Height / 3f), SKTextAlign.Left, unitFont, unitPaint);
    }
}
