using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>One labeled metric readout row (dashboard card or overlay line).</summary>
public sealed record FrameTimeMetric(string Label, string Value);

/// <summary>
/// Everything the widget draws that is a *fact* about the snapshot: the hero
/// strings, the eight dashboard cards, the nine overlay rows, and the
/// visibility decisions that depend on the placement size. The render methods
/// become thin adapters that lay these out — the display rules are assertable
/// without pixels.
/// </summary>
public sealed record FrameTimeDisplay(
    string HeroFps,
    string HeroFrameTimeMs,
    IReadOnlyList<FrameTimeMetric> Dashboard,
    IReadOnlyList<FrameTimeMetric> Overlay,
    string ProcessName,
    bool ShowProcessName,
    bool IsCompact,
    bool ShowGraph,
    bool ShowMetricCards,
    bool ShowSecondRow,
    int OverlayLineCount);

/// <summary>
/// Pure presentation rules for the FPS / Frame Time widget. Zero is
/// PresentMon's own value for no presents (0 presents/sec), so the no-process
/// state and a tracked-but-idle process read identically: numeric values
/// render 0, only the present mode has no numeric zero and renders "—".
/// </summary>
public static class FrameTimePresentation
{
    public static FrameTimeDisplay Build(FrameTimeSnapshotDto snapshot, SKSize bounds)
    {
        // The overlay's frame-time rows derive from the percentile FPS values,
        // matching PresentMon's 99th/1st %tile stat naming (the producer keeps
        // the percentile data in PresentMon's own units).
        string PercentileFrameTime(double percentileFps) => DisplayFormat.Ms(1000.0 / percentileFps);

        var dashboard = new List<FrameTimeMetric>(8)
        {
            new("1% LOW", DisplayFormat.Fps(snapshot.Low1PercentFps)),
            new("0.1% LOW", DisplayFormat.Fps(snapshot.Low01PercentFps)),
            new("CPU FRAME", DisplayFormat.Ms(snapshot.CpuFrameTimeMs)),
            new("GPU BUSY", DisplayFormat.Pct(snapshot.GpuBusyPercent)),
            new("DISPLAYED", DisplayFormat.Fps(snapshot.DisplayedFps)),
            new("DROPPED", DisplayFormat.Count(snapshot.DroppedFrames)),
            new("GPU TIME", DisplayFormat.Ms(snapshot.GpuTimeMs)),
            new("PRESENT MODE", PresentMonPresentMode.ShortName(snapshot.PresentModeId)),
        };

        var overlay = new List<FrameTimeMetric>(9)
        {
            new("Presented FPS", DisplayFormat.FpsValue(snapshot.Fps)),
            new("Displayed FPS", DisplayFormat.FpsValue(snapshot.DisplayedFps)),
            new("99th %tile Frame Time", PercentileFrameTime(snapshot.Low1PercentFps)),
            new("1st %tile Frame Time", PercentileFrameTime(snapshot.Low01PercentFps)),
            new("GPU Busy %", DisplayFormat.Pct(snapshot.GpuBusyPercent)),
            new("GPU Time", DisplayFormat.Ms(snapshot.GpuTimeMs)),
            new("CPU Frame Time", DisplayFormat.Ms(snapshot.CpuFrameTimeMs)),
            new("Dropped Frames", DisplayFormat.Count(snapshot.DroppedFrames)),
            new("Present Mode", PresentMonPresentMode.FullName(snapshot.PresentModeId)),
        };

        return new FrameTimeDisplay(
            HeroFps: DisplayFormat.FpsValue(snapshot.Fps),
            HeroFrameTimeMs: DisplayFormat.Ms(snapshot.FrameTimeMs),
            Dashboard: dashboard,
            Overlay: overlay,
            ProcessName: snapshot.ProcessName,
            ShowProcessName: snapshot.ProcessId > 0 && !string.IsNullOrWhiteSpace(snapshot.ProcessName),
            IsCompact: bounds.Height < 150f,
            ShowGraph: bounds.Height >= 150f && snapshot.RecentFrameTimesMs.Count >= 2,
            ShowMetricCards: bounds.Width >= 410f,
            ShowSecondRow: bounds.Width >= 520f,
            OverlayLineCount: bounds.Height switch { < 110f => 1, < 150f => 4, _ => 9 });
    }
}
