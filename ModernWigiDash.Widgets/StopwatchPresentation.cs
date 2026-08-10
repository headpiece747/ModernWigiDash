using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Pure display rules for the stopwatch widget: the elapsed-time format, the
/// tap status line, and the running/stopped indicator color — previously
/// inline in the render path.
/// </summary>
public static class StopwatchPresentation
{
    /// <summary>"mm:ss.cc" (centiseconds); minutes roll over without an hours
    /// field — the widget's long-standing format.</summary>
    public static string FormatElapsed(TimeSpan total)
        => $"{total.Minutes:D2}:{total.Seconds:D2}.{total.Milliseconds / 10:D2}";

    /// <summary>The tap prompt under the time.</summary>
    public static string StatusText(bool running)
        => running ? "TAP TO PAUSE" : "TAP TO START";

    /// <summary>The running/stopped indicator dot.</summary>
    public static SKColor StatusColor(bool running)
        => running ? new SKColor(239, 68, 68) : new SKColor(34, 197, 94);
}
