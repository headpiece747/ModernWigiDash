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
[WidgetMetadata("frame_time", "FPS / Frame Time", Description = "Live FPS, frame time, 1% low, 0.1% low, GPU busy, and CPU frame time for the most active game. Captured in-process via Windows ETW (DXGI/D3D9/DxgKrnl) by the service — no external tool required.", Author = "ModernWigiDash", Version = "1.0.0", Category = "System Monitoring", DefaultGridSize = GridSizePreset.Size2x2)]
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
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    private const int EnumCurrentSettings = -1;

    private static readonly Lazy<int> MonitorRefreshRateHz = new(() =>
    {
        try
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
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
    private struct DevMode
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

        // The store owns the staleness decision; a stale snapshot renders the
        // unavailable state instead of frozen data.
        FrameTimeSnapshotRecord? snapshot = FrameTimeStore.TryReadFresh();
        if (snapshot == null || !snapshot.IsAvailable)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Frame capture unavailable", "Install and run the PresentMon Service", text);
            return;
        }

        if (!snapshot.CaptureHealthy)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "PresentMon capture inactive", "The service is not producing present data", text);
            return;
        }

        if (snapshot.ProcessId <= 0)
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
            var processFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, procSize);
            using var processPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            string process = TextRenderHelper.TruncateText(snapshot.ProcessName, processFont, bounds.Width - pad * 2f);
            canvas.DrawTextWithFallback(process, bounds.Right - pad - FontHelper.MeasureTextWithFallback(process, processFont), contentTop + procSize, processFont, processPaint);
            heroTop = contentTop + procSize + 6f;
        }

        // Main Hero FPS & Frame Time Section (Largest Typography)
        float heroBottom = showMetrics ? contentTop + (contentBottom - contentTop) * 0.55f : contentBottom;
        float heroH = Math.Max(8f, heroBottom - heroTop);

        // Big Hero FPS Value (Largest Font Size!)
        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        // The snapshot record is replaced ~1/s by the store, so the formatted
        // strings are cached per snapshot instead of re-interpolated 30×/s.
        RefreshCachedStrings(snapshot);
        string fpsText = _cachedFpsText;
        fpsFont.MeasureText(fpsText, out var fpsBounds, fpsPaint);

        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawTextWithFallback(fpsText, fpsX, fpsBaseline, fpsFont, fpsPaint);

        // "FPS" Label & Frame Time (ms) stacked next to big FPS number
        float unitX = fpsX + fpsBounds.Width + 10f;
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawTextWithFallback("FPS", unitX, heroTop + fpsFontSize * 0.38f, unitFont, unitPaint);

        var msFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.36f);
        using var msPaint = new SKPaint { Color = text.WithAlpha(220), IsAntialias = true };
        canvas.DrawTextWithFallback(_cachedMsText, unitX, fpsBaseline, msFont, msPaint);

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

                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 0.5f, gridTop, "1% LOW", _cachedLow1, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 1.5f, gridTop, "0.1% LOW", _cachedLow01, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 2.5f, gridTop, "GPU BUSY", _cachedGpu, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 3.5f, gridTop, "CPU FRAME", _cachedCpu, metricValSize, metricLblSize, accent);
            }
        }

        // Frame-Time Graph (~15% height, auto-hides when container height < 150px)
        if (showGraph)
        {
            SKRect graphArea = new SKRect(bounds.Left + pad, bounds.Bottom - pad - graphHeight, bounds.Right - pad, bounds.Bottom - pad);
            DrawCachedSparkline(canvas, graphArea, snapshot.RecentFrameTimesMs, accent);
        }
    }

    private FrameTimeSnapshotRecord? _lastStringSnapshot;
    private string _cachedFpsText = "";
    private string _cachedMsText = "";
    private string _cachedLow1 = "";
    private string _cachedLow01 = "";
    private string _cachedGpu = "";
    private string _cachedCpu = "";

    /// <summary>
    /// Formats the snapshot strings once per snapshot instance (the store swaps
    /// the record ~1/s) instead of per render at 30 FPS.
    /// </summary>
    private void RefreshCachedStrings(FrameTimeSnapshotRecord snapshot)
    {
        if (ReferenceEquals(snapshot, _lastStringSnapshot)) return;
        _lastStringSnapshot = snapshot;
        _cachedFpsText = snapshot.Fps.ToString("F0", CultureInfo.InvariantCulture);
        _cachedMsText = $"{snapshot.FrameTimeMs:F1} ms";
        _cachedLow1 = $"{snapshot.Low1PercentFps:F0} FPS";
        _cachedLow01 = $"{snapshot.Low01PercentFps:F0} FPS";
        _cachedGpu = $"{snapshot.GpuBusyMs:F1} ms";
        _cachedCpu = $"{snapshot.CpuFrameTimeMs:F1} ms";
    }

    private IReadOnlyList<double>? _lastSparkSamples;
    private SKPath? _sparkFill;
    private SKPath? _sparkLine;

    /// <summary>
    /// The sparkline path is rebuilt only when the samples change (~1/s); the
    /// render tick just draws the cached path.
    /// </summary>
    private void DrawCachedSparkline(SKCanvas canvas, SKRect area, IReadOnlyList<double> samples, SKColor accent)
    {
        if (!ReferenceEquals(samples, _lastSparkSamples))
        {
            _sparkFill?.Dispose();
            _sparkLine?.Dispose();
            _sparkFill = null;
            _sparkLine = null;
            _lastSparkSamples = samples;
            if (samples.Count >= 2)
            {
                double lo = samples.Min();
                double hi = samples.Max();
                if (hi - lo < 0.001)
                {
                    lo -= 1;
                    hi += 1;
                }
                TextRenderHelper.BuildSparklinePaths(area, samples, lo, hi, out _sparkLine, out _sparkFill);
            }
        }

        if (_sparkLine == null || _sparkFill == null) return;

        using var fillPaint = new SKPaint { Color = accent.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(_sparkFill, fillPaint);
        using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.DrawPath(_sparkLine, linePaint);
    }

    private static void DrawMetricCard(SKCanvas canvas, float cx, float topY, string label, string value, float valSize, float lblSize, SKColor accent)
    {
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, valSize);
        using var valPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        valFont.MeasureText(value, out var valBounds, valPaint);
        float valY = topY + valSize * 0.85f;
        canvas.DrawTextWithFallback(value, cx - valBounds.Width / 2f, valY, valFont, valPaint);

        var lblFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, lblSize);
        using var lblPaint = new SKPaint { Color = accent, IsAntialias = true };
        lblFont.MeasureText(label, out var lblBounds, lblPaint);
        float lblY = valY + lblSize + 4f;
        canvas.DrawTextWithFallback(label, cx - lblBounds.Width / 2f, lblY, lblFont, lblPaint);
    }

    private static void DrawMonitorMode(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);
        float heroTop = bounds.Top + pad;
        float heroH = Math.Max(8f, bounds.Height - pad * 2f);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        string fpsText = MonitorRefreshRateHz.Value.ToString(CultureInfo.InvariantCulture);
        fpsFont.MeasureText(fpsText, out var fpsBounds, fpsPaint);

        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawTextWithFallback(fpsText, fpsX, fpsBaseline, fpsFont, fpsPaint);

        float unitX = fpsX + fpsBounds.Width + 10f;
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawTextWithFallback("FPS", unitX, heroTop + fpsFontSize * 0.38f, unitFont, unitPaint);

        var capFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 13f);
        using var capPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        string cap = "MONITOR";
        canvas.DrawTextWithFallback(cap, bounds.Right - pad - FontHelper.MeasureTextWithFallback(cap, capFont), heroTop + 13f, capFont, capPaint);
    }
}
