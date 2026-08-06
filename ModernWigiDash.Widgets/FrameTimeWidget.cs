using System.Globalization;
using System.Runtime.InteropServices;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Live FPS / frame-time dashboard: current FPS, frame time, 1% low, 0.1% low,
/// GPU busy %, and CPU frame time for the focused game/app, plus a rolling
/// frame-time sparkline. Data is captured in-process by the ModernWigiDash
/// service via Windows ETW (DXGI / D3D9 / DxgKrnl present events) — no separate
/// tool such as PresentMon, MSI Afterburner, or RTSS needs to be running.
/// When no DirectX app is focused, the monitor's refresh rate is shown.
/// </summary>
[WidgetMetadata(
    "frame_time",
    "FPS / Frame Time",
    "Live FPS, frame time, 1% low, 0.1% low, GPU busy, and CPU frame time for the most active game. Captured in-process via Windows ETW (DXGI/D3D9/DxgKrnl) by the service — no external tool required.",
    "ModernWigiDash",
    "1.0.0",
    "System Monitoring",
    GridSizePreset.Size2x2)]
public class FrameTimeWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Show Process", WidgetPropertyType.Boolean, "Show the tracked game/process name", true)]
    public bool ShowProcess { get; set; } = true;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private const int EnumCurrentSettings = -1;

    private static readonly Lazy<int> MonitorRefreshRateHz = new(() =>
    {
        try
        {
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency > 0)
            {
                return mode.dmDisplayFrequency;
            }
        }
        catch (Exception)
        {
            // Fall through to 60 Hz default
            System.Diagnostics.Debug.WriteLine("Failed to query monitor refresh rate; defaulting to 60 Hz");
        }
        return 60;
    });

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accent = SKColor.TryParse(AccentColorHex, out var parsed) ? parsed : new SKColor(255, 205, 133);
        SKColor text = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;

        FrameTimeSnapshotRecord snapshot = FrameTimeStore.ReadSnapshot();
        if (!snapshot.IsAvailable)
        {
            DrawPlaceholder(canvas, bounds, "Frame capture unavailable", "Run the service with admin/SYSTEM rights", text);
            return;
        }

        if (snapshot.IsAvailable && snapshot.ProcessId <= 0)
        {
            // No process targeted (desktop / static window focused, or the App
            // itself): show the monitor refresh rate as the FPS.
            DrawMonitorMode(canvas, bounds, accent, text);
            return;
        }

        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);

        // Auto-hide rules:
        //  - Below 150px height, everything hides except the large FPS block.
        //  - Below 410px width, the secondary metric cards (1% low, 0.1% low,
        //    GPU busy, CPU frame) hide as well.
        bool tiny = bounds.Height < 150f;
        bool showMetrics = bounds.Width >= 410f;

        // Auto-hides graph when container height is below 150px
        bool showGraph = bounds.Height >= 150f && snapshot.RecentFrameTimesMs.Count >= 2;
        float graphHeight = showGraph ? bounds.Height * 0.15f : 0f;

        float contentTop = bounds.Top + pad;
        float contentBottom = bounds.Bottom - pad - (showGraph ? graphHeight + 6f : 0f);

        // Process name line (top-right). Hidden in tiny mode.
        float heroTop = contentTop;
        if (!tiny && ShowProcess && !string.IsNullOrWhiteSpace(snapshot.ProcessName))
        {
            float procSize = Math.Clamp((contentBottom - contentTop) * 0.08f, 10f, 15f);
            using var processFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Normal), procSize);
            FontHelper.ConfigureHighQualityFont(processFont);
            using var processPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            string process = TruncateToWidth(snapshot.ProcessName, processFont, bounds.Width - pad * 2f);
            canvas.DrawText(process, bounds.Right - pad - processFont.MeasureText(process), contentTop + procSize, SKTextAlign.Left, processFont, processPaint);
            heroTop = contentTop + procSize + 6f;
        }

        // Main Hero FPS & Frame Time Section (Largest Typography)
        float heroBottom = showMetrics ? contentTop + (contentBottom - contentTop) * 0.55f : contentBottom;
        float heroH = Math.Max(8f, heroBottom - heroTop);

        // Big Hero FPS Value (Largest Font Size!)
        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        using var fpsFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), fpsFontSize);
        FontHelper.ConfigureHighQualityFont(fpsFont);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        string fpsText = snapshot.Fps.ToString("F0", CultureInfo.InvariantCulture);
        fpsFont.MeasureText(fpsText, out var fpsBounds, fpsPaint);

        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawText(fpsText, fpsX, fpsBaseline, SKTextAlign.Left, fpsFont, fpsPaint);

        // "FPS" Label & Frame Time (ms) stacked next to big FPS number
        float unitX = fpsX + fpsBounds.Width + 10f;
        using var unitFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), fpsFontSize * 0.32f);
        FontHelper.ConfigureHighQualityFont(unitFont);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawText("FPS", unitX, heroTop + fpsFontSize * 0.38f, SKTextAlign.Left, unitFont, unitPaint);

        using var msFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), fpsFontSize * 0.36f);
        FontHelper.ConfigureHighQualityFont(msFont);
        using var msPaint = new SKPaint { Color = text.WithAlpha(220), IsAntialias = true };
        string msText = $"{snapshot.FrameTimeMs:F1} ms";
        canvas.DrawText(msText, unitX, fpsBaseline, SKTextAlign.Left, msFont, msPaint);

        // Secondary Metrics Grid (1% Low, 0.1% Low, GPU Busy, CPU Frame)
        // Auto-hides when container width is below 410px.
        if (showMetrics)
        {
            float gridTop = heroBottom + 4f;
            float gridH = contentBottom - gridTop;
            if (gridH >= 24f)
            {
                float colWidth = (bounds.Width - pad * 2f) / 4f;
                float metricValSize = Math.Clamp(gridH * 0.44f, 12f, 36f);
                float metricLblSize = Math.Clamp(gridH * 0.28f, 9f, 20f);

                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 0.5f, gridTop, colWidth, gridH, "1% LOW", $"{snapshot.Low1PercentFps:F0} FPS", metricValSize, metricLblSize, accent, text);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 1.5f, gridTop, colWidth, gridH, "0.1% LOW", $"{snapshot.Low01PercentFps:F0} FPS", metricValSize, metricLblSize, accent, text);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 2.5f, gridTop, colWidth, gridH, "GPU BUSY", $"{snapshot.GpuBusyPercent:F0}%", metricValSize, metricLblSize, accent, text);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 3.5f, gridTop, colWidth, gridH, "CPU FRAME", $"{snapshot.CpuFrameTimeMs:F1} ms", metricValSize, metricLblSize, accent, text);
            }
        }

        // Frame-Time Graph (~15% height, auto-hides when container height < 150px)
        if (showGraph)
        {
            SKRect graphArea = new SKRect(bounds.Left + pad, bounds.Bottom - pad - graphHeight, bounds.Right - pad, bounds.Bottom - pad);
            DrawSparkline(canvas, graphArea, snapshot.RecentFrameTimesMs, accent);
        }
    }

    private static void DrawMetricCard(SKCanvas canvas, float cx, float topY, float width, float height, string label, string value, float valSize, float lblSize, SKColor accent, SKColor text)
    {
        using var valFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), valSize);
        FontHelper.ConfigureHighQualityFont(valFont);
        using var valPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        valFont.MeasureText(value, out var valBounds, valPaint);
        float valY = topY + valSize * 0.85f;
        canvas.DrawText(value, cx - valBounds.Width / 2f, valY, SKTextAlign.Left, valFont, valPaint);

        using var lblFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), lblSize);
        FontHelper.ConfigureHighQualityFont(lblFont);
        using var lblPaint = new SKPaint { Color = accent, IsAntialias = true };
        lblFont.MeasureText(label, out var lblBounds, lblPaint);
        float lblY = valY + lblSize + 4f;
        canvas.DrawText(label, cx - lblBounds.Width / 2f, lblY, SKTextAlign.Left, lblFont, lblPaint);
    }

    private static void DrawMonitorMode(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);
        float heroTop = bounds.Top + pad;
        float heroH = Math.Max(8f, bounds.Height - pad * 2f);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        using var fpsFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), fpsFontSize);
        FontHelper.ConfigureHighQualityFont(fpsFont);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        string fpsText = MonitorRefreshRateHz.Value.ToString(CultureInfo.InvariantCulture);
        fpsFont.MeasureText(fpsText, out var fpsBounds, fpsPaint);

        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawText(fpsText, fpsX, fpsBaseline, SKTextAlign.Left, fpsFont, fpsPaint);

        float unitX = fpsX + fpsBounds.Width + 10f;
        using var unitFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), fpsFontSize * 0.32f);
        FontHelper.ConfigureHighQualityFont(unitFont);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawText("FPS", unitX, heroTop + fpsFontSize * 0.38f, SKTextAlign.Left, unitFont, unitPaint);

        using var capFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Normal), 13f);
        FontHelper.ConfigureHighQualityFont(capFont);
        using var capPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        string cap = "MONITOR";
        canvas.DrawText(cap, bounds.Right - pad - capFont.MeasureText(cap), heroTop + 13f, SKTextAlign.Left, capFont, capPaint);
    }

    private static void DrawSparkline(SKCanvas canvas, SKRect area, IReadOnlyList<double> samples, SKColor accent)
    {
        double lo = samples.Min();
        double hi = samples.Max();
        if (hi - lo < 0.001)
        {
            lo -= 1;
            hi += 1;
        }

        float span = area.Width / Math.Max(1, samples.Count - 1);
        var line = new SKPath();
        var fill = new SKPath();
        bool first = true;
        for (int i = 0; i < samples.Count; i++)
        {
            float x = area.Left + i * span;
            float y = area.Bottom - (float)((samples[i] - lo) / (hi - lo)) * area.Height;
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
        }

        fill.LineTo(area.Right, area.Bottom);
        fill.LineTo(area.Left, area.Bottom);
        fill.Close();

        using var fillPaint = new SKPaint { Color = accent.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(fill, fillPaint);

        using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.DrawPath(line, linePaint);
    }

    private static void DrawPlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text)
    {
        using var titleFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), 16f);
        FontHelper.ConfigureHighQualityFont(titleFont);
        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        titleFont.MeasureText(title, out var titleBounds, titlePaint);
        canvas.DrawText(title, bounds.MidX - titleBounds.Width / 2f, bounds.MidY - 2f, SKTextAlign.Left, titleFont, titlePaint);

        using var subFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Normal), 11f);
        FontHelper.ConfigureHighQualityFont(subFont);
        using var subPaint = new SKPaint { Color = text.WithAlpha(150), IsAntialias = true };
        subFont.MeasureText(subtitle, out var subBounds, subPaint);
        canvas.DrawText(subtitle, bounds.MidX - subBounds.Width / 2f, bounds.MidY + 20f, SKTextAlign.Left, subFont, subPaint);
    }

    private static string TruncateToWidth(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth || maxWidth <= 0)
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
