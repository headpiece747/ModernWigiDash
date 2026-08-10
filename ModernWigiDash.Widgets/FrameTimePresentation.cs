using System.Globalization;
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
        string Fps0(double v) => v > 0 ? $"{v:F0} FPS" : "0 FPS";
        string Ms0(double v) => double.IsFinite(v) && v > 0 ? $"{v:F1} ms" : "0.0 ms";
        string Pct0(double v) => v > 0 ? $"{v:F0}%" : "0%";
        string Count0(int v) => v.ToString(CultureInfo.InvariantCulture);
        string FpsHero(double v) => v > 0 ? $"{v:F0}" : "0";

        // The overlay's frame-time rows derive from the percentile FPS values,
        // matching PresentMon's 99th/1st %tile stat naming (the producer keeps
        // the percentile data in PresentMon's own units).
        string PercentileFrameTime(double percentileFps) => Ms0(1000.0 / percentileFps);

        var dashboard = new List<FrameTimeMetric>(8)
        {
            new("1% LOW", Fps0(snapshot.Low1PercentFps)),
            new("0.1% LOW", Fps0(snapshot.Low01PercentFps)),
            new("CPU FRAME", Ms0(snapshot.CpuFrameTimeMs)),
            new("GPU BUSY", Pct0(snapshot.GpuBusyPercent)),
            new("DISPLAYED", Fps0(snapshot.DisplayedFps)),
            new("DROPPED", Count0(snapshot.DroppedFrames)),
            new("GPU TIME", Ms0(snapshot.GpuTimeMs)),
            new("PRESENT MODE", PresentMonPresentMode.ShortName(snapshot.PresentModeId)),
        };

        var overlay = new List<FrameTimeMetric>(9)
        {
            new("Presented FPS", FpsHero(snapshot.Fps)),
            new("Displayed FPS", FpsHero(snapshot.DisplayedFps)),
            new("99th %tile Frame Time", PercentileFrameTime(snapshot.Low1PercentFps)),
            new("1st %tile Frame Time", PercentileFrameTime(snapshot.Low01PercentFps)),
            new("GPU Busy %", Pct0(snapshot.GpuBusyPercent)),
            new("GPU Time", Ms0(snapshot.GpuTimeMs)),
            new("CPU Frame Time", Ms0(snapshot.CpuFrameTimeMs)),
            new("Dropped Frames", Count0(snapshot.DroppedFrames)),
            new("Present Mode", PresentMonPresentMode.FullName(snapshot.PresentModeId)),
        };

        return new FrameTimeDisplay(
            HeroFps: FpsHero(snapshot.Fps),
            HeroFrameTimeMs: Ms0(snapshot.FrameTimeMs),
            Dashboard: dashboard,
            Overlay: overlay,
            ProcessName: snapshot.ProcessName,
            ShowProcessName: snapshot.ProcessId > 0 && !string.IsNullOrWhiteSpace(snapshot.ProcessName),
            ShowGraph: bounds.Height >= 150f && snapshot.RecentFrameTimesMs.Count >= 2,
            ShowMetricCards: bounds.Width >= 410f,
            ShowSecondRow: bounds.Width >= 520f,
            OverlayLineCount: bounds.Height switch { < 110f => 1, < 150f => 4, _ => 9 });
    }
}
