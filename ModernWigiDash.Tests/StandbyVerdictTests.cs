using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// StandbyVerdict at its interface: the one spelling of the standby verdict's
/// log vocabulary that both engine entry points (the session-end
/// TryGoToStandby and the dispose path) route through. Pinned without an
/// engine or a transport — the four line shapes (failure, settled-not-confirmed,
/// abandoned-hang, each for both entry points) and the silent cases (confirmed,
/// no device) are assertable where they live. The engine-level pins in
/// DisplayDeviceEngineTests still cover the routing end to end.
/// </summary>
[TestClass]
public class StandbyVerdictTests
{
    [TestMethod]
    public void FailureLine_SessionEnd_CarriesTheVendorWording()
    {
        var ex = new InvalidOperationException("pipe reset");

        Assert.AreEqual("Standby failed: pipe reset", StandbyVerdict.FailureLine(ex, duringDispose: false));
    }

    [TestMethod]
    public void FailureLine_Dispose_CarriesTheDisposePrefix()
    {
        var ex = new InvalidOperationException("pipe reset");

        Assert.AreEqual("Standby failed during dispose: pipe reset", StandbyVerdict.FailureLine(ex, duringDispose: true));
    }

    [TestMethod]
    public void NotConfirmedLine_SettledButFailed_ReportsTheWrites()
    {
        Assert.AreEqual(
            "Standby NOT confirmed: the standby control writes did not succeed — the display may stay lit",
            StandbyVerdict.NotConfirmedLine(settled: true, confirmed: false, hasTransport: true, duringDispose: false));
        Assert.AreEqual(
            "Standby NOT confirmed during dispose: the standby control writes did not succeed — the display may stay lit",
            StandbyVerdict.NotConfirmedLine(settled: true, confirmed: false, hasTransport: true, duringDispose: true));
    }

    [TestMethod]
    public void NotConfirmedLine_AbandonedHang_ReportsTheExpiredWait()
    {
        Assert.AreEqual(
            "Standby NOT confirmed: the bounded wait expired — a hung standby was abandoned, and the display may stay lit",
            StandbyVerdict.NotConfirmedLine(settled: false, confirmed: false, hasTransport: true, duringDispose: false));
        Assert.AreEqual(
            "Standby NOT confirmed during dispose: the bounded close wait expired — a hung standby was abandoned at exit, and the display may stay lit",
            StandbyVerdict.NotConfirmedLine(settled: false, confirmed: false, hasTransport: true, duringDispose: true));
    }

    [TestMethod]
    public void NotConfirmedLine_ConfirmedOrNoDevice_IsSilent()
    {
        Assert.IsNull(StandbyVerdict.NotConfirmedLine(settled: true, confirmed: true, hasTransport: true, duringDispose: false));
        Assert.IsNull(StandbyVerdict.NotConfirmedLine(settled: true, confirmed: true, hasTransport: true, duringDispose: true));
        Assert.IsNull(StandbyVerdict.NotConfirmedLine(settled: true, confirmed: false, hasTransport: false, duringDispose: true));
    }
}
