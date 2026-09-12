namespace ModernWigiDash.Tests;

/// <summary>
/// StartupMinimizeLatchPolicy at its interface: the one-shot latch semantics
/// that keep the autostart minimize from being swallowed by the minimize-to-tray
/// intercept, pinned without a window or WPF events. The production binding in
/// WindowLifecycle routes every arm/clear/consume through this module; the
/// existing WindowAutostartTests still cover the end-to-end behavior on a live
/// STA window.
/// </summary>
[TestClass]
public class StartupMinimizeLatchPolicyTests
{
    [TestMethod]
    public void Arm_DisarmedState_ReturnsArmed()
    {
        var result = StartupMinimizeLatchPolicy.Arm(new StartupMinimizeLatchPolicy.LatchState(false));

        Assert.IsTrue(result.Armed);
    }

    [TestMethod]
    public void Clear_ArmedState_ReturnsDisarmed()
    {
        var result = StartupMinimizeLatchPolicy.Clear(new StartupMinimizeLatchPolicy.LatchState(true));

        Assert.IsFalse(result.Armed);
    }

    [TestMethod]
    public void Consume_ArmedState_VetoesAndClears()
    {
        var (veto, next) = StartupMinimizeLatchPolicy.Consume(new StartupMinimizeLatchPolicy.LatchState(true));

        Assert.IsTrue(veto, "an armed latch vetoes the state change");
        Assert.IsFalse(next.Armed, "the latch is one-shot: consumed and cleared");
    }

    [TestMethod]
    public void Consume_DisarmedState_AllowsAndStaysDisarmed()
    {
        var (veto, next) = StartupMinimizeLatchPolicy.Consume(new StartupMinimizeLatchPolicy.LatchState(false));

        Assert.IsFalse(veto, "a disarmed latch allows the state change");
        Assert.IsFalse(next.Armed);
    }

    [TestMethod]
    public void Consume_Twice_SecondConsumeDoesNotVeto()
    {
        var (_, afterFirst) = StartupMinimizeLatchPolicy.Consume(new StartupMinimizeLatchPolicy.LatchState(true));
        var (secondVeto, _) = StartupMinimizeLatchPolicy.Consume(afterFirst);

        Assert.IsFalse(secondVeto, "the latch is one-shot: a second consume does not veto");
    }
}
