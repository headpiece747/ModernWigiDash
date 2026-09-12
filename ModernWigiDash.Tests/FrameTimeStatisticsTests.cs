namespace ModernWigiDash.Tests;

[TestClass]
public class FrameTimeStatisticsTests
{
    [TestMethod]
    public void FrameTimeStatistics_Percentile_UsesNearestRank()
    {
        double[] values = [10, 20, 30, 40];

        Assert.AreEqual(10, FrameTimeStatistics.Percentile(values, 0));
        Assert.AreEqual(20, FrameTimeStatistics.Percentile(values, 50));
        Assert.AreEqual(40, FrameTimeStatistics.Percentile(values, 99));
        Assert.AreEqual(40, FrameTimeStatistics.Percentile(values, 100));
        Assert.AreEqual(0, FrameTimeStatistics.Percentile([], 99));
    }

    [TestMethod]
    public void FrameTimeStatistics_Percentile_SingleSample_ReturnsThatSample()
    {
        // A one-sample window has no spread: every percentile is the sample
        // itself. Pinned for both the stack-alloc fast path (a double[] is an
        // ICollection<double>) and the LINQ fallback (a query result is not),
        // so neither single-element branch can silently regress.
        Assert.AreEqual(42.0, FrameTimeStatistics.Percentile([42.0], 50));
        Assert.AreEqual(42.0, FrameTimeStatistics.Percentile(new[] { 42.0 }.OrderBy(v => v), 50));
    }

    [TestMethod]
    public void FrameTimeStatistics_Percentile_EmptyEnumerable_FallbackPath_ReturnsZero()
    {
        // An empty enumerable that is NOT a small ICollection<double> (a LINQ
        // query result) takes the fallback path's empty branch, distinct from
        // the fast path's empty branch (an empty double[] is an ICollection).
        IEnumerable<double> empty = Enumerable.Empty<double>().OrderBy(v => v);
        Assert.AreEqual(0, FrameTimeStatistics.Percentile(empty, 50));
    }

    [TestMethod]
    public void FrameTimeStatistics_LowFps_ConvertsFromFrameTimes()
    {
        var frameTimes = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray();

        double low1 = FrameTimeStatistics.Low1PercentFps(frameTimes);
        double low01 = FrameTimeStatistics.Low01PercentFps(frameTimes);

        Assert.AreEqual(1000.0 / 989.0, low1, 0.001, "1% low should use the 99th percentile frame time");
        Assert.AreEqual(1000.0 / 998.0, low01, 0.001, "0.1% low should use the 99.9th percentile frame time");
    }

    [TestMethod]
    public void FrameTimeStatistics_FpsFromFrameTimeMs_HandlesEdgeCases()
    {
        Assert.AreEqual(60.0, FrameTimeStatistics.FpsFromFrameTimeMs(1000.0 / 60.0), 0.001);
        Assert.AreEqual(0.0, FrameTimeStatistics.FpsFromFrameTimeMs(0));
        Assert.AreEqual(0.0, FrameTimeStatistics.FpsFromFrameTimeMs(-5));
    }
}
