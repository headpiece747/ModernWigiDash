namespace ModernWigiDash.Sdk;

/// <summary>
/// Pure, testable statistics used to turn a stream of per-frame timings into
/// the FPS / frame-time / low-percentile metrics shown by the frame-time widget.
/// Frame times are expressed in milliseconds; FPS conversions use 1000 / ms.
/// </summary>
public static class FrameTimeStatistics
{
    /// <summary>The rolling sparkline window (samples) � the single owner of
    /// the cap the producer's frame-time buffer honors.</summary>
    public const int MaxSparklineSamples = 240;

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
    /// times using the nearest-rank method. Returns 0 for an empty set or a
    /// NaN percentile. Example: percentile 99 is the 1% low frame time,
    /// percentile 99.9 the 0.1% low frame time.
    /// </summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        // Explicit NaN guard: Math.Clamp propagates NaN, and casting NaN to
        // int yields 0 — a NaN percentile would silently read as the minimum
        // instead of "no data".
        if (double.IsNaN(percentile)) return 0;

        // Zero-alloc fast path: stack-allocated copy + in-place sort for the
        // common small sample window (<= 512); LINQ fallback for larger sets.
        if (values is ICollection<double> collection && collection.Count <= 512)
        {
            Span<double> sorted = stackalloc double[collection.Count];
            int i = 0;
            foreach (double v in values)
            {
                sorted[i++] = v;
            }
            if (sorted.Length == 0)
            {
                return 0;
            }
            if (sorted.Length == 1)
            {
                return sorted[0];
            }

            sorted.Sort();
            return sorted[NearestRankIndex(sorted.Length, percentile)];
        }

        double[] fallback = values.OrderBy(v => v).ToArray();
        if (fallback.Length == 0)
        {
            return 0;
        }

        if (fallback.Length == 1)
        {
            return fallback[0];
        }

        return fallback[NearestRankIndex(fallback.Length, percentile)];
    }

    /// <summary>
    /// Nearest-rank index for a sorted sample of <paramref name="count"/> items
    /// at the given percentile — the single definition of the index math shared
    /// by both sample paths, clamped so the result is always in range.
    /// </summary>
    private static int NearestRankIndex(int count, double percentile)
    {
        double p = Math.Clamp(percentile, 0, 100) / 100.0;
        return Math.Clamp((int)Math.Ceiling(p * count - 1e-9) - 1, 0, count - 1);
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
