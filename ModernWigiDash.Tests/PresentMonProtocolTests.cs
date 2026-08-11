using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the PresentMon protocol constants (PresentMonAPI.h) — the
/// DisplayProtocolTests shape for the PM wire format: metric ids, stat ids,
/// and the dynamic-query window/offset tuning. Drift from the header fails
/// loudly here instead of mis-registering silently against the service.
/// </summary>
[TestClass]
public class PresentMonProtocolTests
{
    [TestMethod]
    public void MetricIds_AreExact()
    {
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual(8u, PresentMonProtocol.MetricCpuFrameTime);
        Assert.AreEqual(11u, PresentMonProtocol.MetricDisplayedFps);
        Assert.AreEqual(12u, PresentMonProtocol.MetricPresentedFps);
        Assert.AreEqual(13u, PresentMonProtocol.MetricGpuTime);
        Assert.AreEqual(14u, PresentMonProtocol.MetricGpuBusy);
        Assert.AreEqual(16u, PresentMonProtocol.MetricDroppedFrames);
        Assert.AreEqual(20u, PresentMonProtocol.MetricPresentMode);
        Assert.AreEqual(78u, PresentMonProtocol.MetricBetweenPresents);
#pragma warning restore MSTEST0025, MSTEST0032
    }

    [TestMethod]
    public void StatIds_AreExact()
    {
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual(0u, PresentMonProtocol.StatNone);
        Assert.AreEqual(1u, PresentMonProtocol.StatAvg);
        Assert.AreEqual(5u, PresentMonProtocol.StatPercentile01);
        Assert.AreEqual(12u, PresentMonProtocol.StatNewestPoint);
#pragma warning restore MSTEST0025, MSTEST0032
    }

    [TestMethod]
    public void DynamicQueryWindow_IsOneSecondWindowTwentyMillisecondsInThePast()
    {
        // The analyzers constant-fold these pins (always-true hints) — the pins
        // ARE the contract, suppressed like DisplayProtocolTests' byte pins.
#pragma warning disable MSTEST0025, MSTEST0032
        Assert.AreEqual(1000.0, PresentMonProtocol.DynamicQueryWindowMs);
        Assert.AreEqual(1020.0, PresentMonProtocol.DynamicQueryOffsetMs);
#pragma warning restore MSTEST0025, MSTEST0032
    }
}
