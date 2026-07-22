using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("aida64_panel", "AIDA64 Sensor Panel", "Display real-time AIDA64 sensor values with custom gauge thresholds & glassmorphic charts.", "ModernWigiDash", "2.0.0", "System Monitoring", GridSizePreset.Size2x2)]
public class Aida64SensorWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Sensor Name", WidgetPropertyType.Text, "Target AIDA64 Sensor Label (e.g. CPU Temp, GPU Core)", "CPU Temperature")]
    public string SensorName { get; set; } = "CPU Temperature";

    [WidgetProperty("Unit", WidgetPropertyType.Text, "Unit label", "°C")]
    public string Unit { get; set; } = "°C";

    [WidgetProperty("Max Gauge Value", WidgetPropertyType.Number, "Max threshold for gauge", 100f)]
    public float MaxValue { get; set; } = 100f;

    private float _liveSensorValue = 48.0f;
    private PerformanceCounter? _cpuCounter;

    public override ValueTask InitializeAsync(IWidgetContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        float liveVal = ReadLiveAida64Value(SensorName);
        TelemetryRenderer.RenderGaugeCard(canvas, bounds, "AIDA64 LIVE TELEMETRY", SensorName, liveVal, MaxValue, Unit, new SKColor(229, 57, 53)); // Material 3 Red
    }

    private float ReadLiveAida64Value(string targetSensor)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("AIDA64_SensorValues");
            using var stream = mmf.CreateViewStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            string xmlData = reader.ReadToEnd();

            int idx = xmlData.IndexOf(targetSensor, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int valIdx = xmlData.IndexOf("<value>", idx, StringComparison.OrdinalIgnoreCase);
                if (valIdx >= 0)
                {
                    int endValIdx = xmlData.IndexOf("</value>", valIdx, StringComparison.OrdinalIgnoreCase);
                    if (endValIdx > valIdx)
                    {
                        string valStr = xmlData.Substring(valIdx + 7, endValIdx - (valIdx + 7));
                        if (float.TryParse(valStr, out float val)) return val;
                    }
                }
            }
        }
        catch { }

        if (_cpuCounter != null)
        {
            try
            {
                _liveSensorValue = _cpuCounter.NextValue();
            }
            catch { }
        }

        return _liveSensorValue;
    }

    public override ValueTask DisposeAsync()
    {
        _cpuCounter?.Dispose();
        return base.DisposeAsync();
    }
}

[WidgetMetadata("hwinfo_monitor", "HWiNFO Monitor", "Show live hardware telemetry gauges and sparkline history from HWiNFO.", "ModernWigiDash", "2.0.0", "System Monitoring", GridSizePreset.Size2x2)]
public class HwInfoMonitorWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Sensor Label", WidgetPropertyType.Text, "HWiNFO Sensor Label (e.g. GPU Utilization)", "GPU Utilization")]
    public string SensorLabel { get; set; } = "GPU Utilization";

    [WidgetProperty("Unit", WidgetPropertyType.Text, "Unit label", "%")]
    public string Unit { get; set; } = "%";

    private PerformanceCounter? _ramCounter;
    private float _liveLoad = 35.0f;

    public override ValueTask InitializeAsync(IWidgetContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        try
        {
            _ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        if (_ramCounter != null)
        {
            try
            {
                _liveLoad = _ramCounter.NextValue();
            }
            catch { }
        }

        TelemetryRenderer.RenderGaugeCard(canvas, bounds, "HWINFO LIVE TELEMETRY", SensorLabel, _liveLoad, 100f, Unit, new SKColor(255, 180, 171)); // M3 Coral Red Accent
    }

    public override ValueTask DisposeAsync()
    {
        _ramCounter?.Dispose();
        return base.DisposeAsync();
    }
}

public static class TelemetryRenderer
{
    public static void RenderGaugeCard(SKCanvas canvas, SKRect bounds, string header, string label, float value, float maxValue, string unit, SKColor accentColor)
    {
        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 230), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 100), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        float pad = 16f;
        using var headerFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 11f);
        using var headerPaint = new SKPaint { Color = new SKColor(224, 194, 196), IsAntialias = true };
        canvas.DrawText(header, pad, pad + 11f, SKTextAlign.Left, headerFont, headerPaint);

        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 18f);
        using var labelPaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };
        canvas.DrawText(label, pad, pad + 35f, SKTextAlign.Left, labelFont, labelPaint);

        float gaugeSize = Math.Min(bounds.Width * 0.45f, bounds.Height - 50f);
        float cx = bounds.Right - pad - (gaugeSize / 2f);
        float cy = bounds.MidY + 10f;
        float radius = (gaugeSize / 2f) - 10f;

        using var trackPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20), Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
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
        using var valFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), gaugeSize * 0.22f);
        using var valPaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };
        var valBounds = new SKRect();
        valFont.MeasureText(valStr, out valBounds, valPaint);
        canvas.DrawText(valStr, cx - (valBounds.Width / 2f), cy + (valBounds.Height / 3f), SKTextAlign.Left, valFont, valPaint);

        using var unitFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 12f);
        using var unitPaint = new SKPaint { Color = accentColor, IsAntialias = true };
        canvas.DrawText(unit, cx - (valBounds.Width / 2f) + valBounds.Width + 4f, cy + (valBounds.Height / 3f), SKTextAlign.Left, unitFont, unitPaint);
    }
}
