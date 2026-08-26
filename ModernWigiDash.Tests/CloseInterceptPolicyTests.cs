namespace ModernWigiDash.Tests;

/// <summary>
/// The window's close-intercept decision pinned without WPF: the only hide
/// is the tray keep-alive with a live tray, the policy's resolve routing
/// keeps a hand-edited profile from smuggling in a hide (case is identity),
/// and the N1 fallback (a dead tray) falls every action through to the
/// normal behavior.
/// </summary>
[TestClass]
public class CloseInterceptPolicyTests
{
    [TestMethod]
    public void ShouldHide_BehaviorOnAndTrayLive_Hides()
        => Assert.IsTrue(CloseInterceptPolicy.ShouldHide(CloseBehaviorPolicy.HideToTray, trayLive: true));

    [TestMethod]
    public void ShouldHide_BehaviorOffAndTrayLive_Exits()
        => Assert.IsFalse(CloseInterceptPolicy.ShouldHide(CloseBehaviorPolicy.Quit, trayLive: true),
            "the pre-feature behavior must keep closing - the tray keep-alive is opt-in");

    [TestMethod]
    public void ShouldHide_BehaviorOnAndTrayDead_FallsThrough()
        => Assert.IsFalse(CloseInterceptPolicy.ShouldHide(CloseBehaviorPolicy.HideToTray, trayLive: false),
            "a dead tray leaves the user no way back to the window - the N1 fallback");

    [TestMethod]
    public void ShouldHide_AbsentOrUnknownBehavior_FallsThrough()
    {
        Assert.IsFalse(CloseInterceptPolicy.ShouldHide(null, trayLive: true), "an absent value takes the default (quit)");
        Assert.IsFalse(CloseInterceptPolicy.ShouldHide("bogus", trayLive: true), "an unknown value takes the default (quit)");
        Assert.IsFalse(CloseInterceptPolicy.ShouldHide("HIDETOTRAY", trayLive: true),
            "case is identity: a hand-edited value is unknown, not a typo'd hide");
    }
}
