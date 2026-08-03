namespace ModernWigiDash.Core.Telemetry;

/// <summary>
/// Pure, testable statistics used to turn a stream of per-frame timings into
/// the FPS / frame-time / low-percentile metrics shown by the frame-time widget.
/// Frame times are expressed in milliseconds; FPS conversions use 1000 / ms.
/// </summary>
public static class FrameTimeStatistics
{
    /// <summary>
    /// Converts a frame time in milliseconds to an FPS value. Returns 0 for
    /// non-positive frame times so callers never divide by zero.
    /// </summary>
    public static double FpsFromFrameTimeMs(double frameTimeMs)
    {
        return frameTimeMs > 0 ? 1000.0 / frameTimeMs : 0;
    }

    /// <summary>
    /// Returns the <paramref name="percentile"/> (0..100) of the given frame
    /// times using the nearest-rank method. Returns 0 for an empty set.
    /// Example: percentile 99 is the 1% low frame time, percentile 99.9 the
    /// 0.1% low frame time.
    /// </summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double p = Math.Clamp(percentile, 0, 100) / 100.0;
        int index = (int)Math.Ceiling(p * sorted.Length - 1e-9) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Returns the 1% low as an FPS value: the FPS equivalent of the 99th
    /// percentile frame time. Returns 0 when no frame times are available.
    /// </summary>
    public static double Low1PercentFps(IEnumerable<double> frameTimesMs)
    {
        return FpsFromFrameTimeMs(Percentile(frameTimesMs, 99));
    }

    /// <summary>
    /// Returns the 0.1% low as an FPS value: the FPS equivalent of the 99.9th
    /// percentile frame time. Returns 0 when no frame times are available.
    /// </summary>
    public static double Low01PercentFps(IEnumerable<double> frameTimesMs)
    {
        return FpsFromFrameTimeMs(Percentile(frameTimesMs, 99.9));
    }
}
