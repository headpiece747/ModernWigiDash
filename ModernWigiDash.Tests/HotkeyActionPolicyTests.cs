namespace ModernWigiDash.Tests;

/// <summary>
/// The hotkey executor's decision rules — previously buried in SendInput
/// P/Invoke methods and untestable; now pure and pinned.
/// </summary>
[TestClass]
public class HotkeyActionPolicyTests
{
    [TestMethod]
    public void ClampRepeat_BoundsOneToTwenty()
    {
        Assert.AreEqual(1, HotkeyActionPolicy.ClampRepeat(0));
        Assert.AreEqual(1, HotkeyActionPolicy.ClampRepeat(-5));
        Assert.AreEqual(5, HotkeyActionPolicy.ClampRepeat(5));
        Assert.AreEqual(20, HotkeyActionPolicy.ClampRepeat(20));
        Assert.AreEqual(20, HotkeyActionPolicy.ClampRepeat(100));
    }

    [TestMethod]
    public void ClampDelayMs_BoundsZeroToFiveSeconds()
    {
        Assert.AreEqual(0, HotkeyActionPolicy.ClampDelayMs(-100));
        Assert.AreEqual(250, HotkeyActionPolicy.ClampDelayMs(250));
        Assert.AreEqual(5000, HotkeyActionPolicy.ClampDelayMs(5000));
        Assert.AreEqual(5000, HotkeyActionPolicy.ClampDelayMs(99999));
    }

    [TestMethod]
    public void IsAllowedUrl_OnlyHttpHttpsMailto()
    {
        Assert.IsTrue(HotkeyActionPolicy.IsAllowedUrl("https://example.com"));
        Assert.IsTrue(HotkeyActionPolicy.IsAllowedUrl("http://example.com"));
        Assert.IsTrue(HotkeyActionPolicy.IsAllowedUrl("mailto:someone@example.com"));
        Assert.IsFalse(HotkeyActionPolicy.IsAllowedUrl("ftp://example.com"));
        Assert.IsFalse(HotkeyActionPolicy.IsAllowedUrl("javascript:alert(1)"));
        Assert.IsFalse(HotkeyActionPolicy.IsAllowedUrl("not a url"));
    }

    [TestMethod]
    public void MouseButtonFlags_NamedButtons_ReturnFlagPairs()
    {
        Assert.AreEqual((0x0008u, 0x0010u), HotkeyActionPolicy.MouseButtonFlags("right"));
        Assert.AreEqual((0x0008u, 0x0010u), HotkeyActionPolicy.MouseButtonFlags("RButton"));
        Assert.AreEqual((0x0020u, 0x0040u), HotkeyActionPolicy.MouseButtonFlags("middle"));
        Assert.AreEqual((0x0002u, 0x0004u), HotkeyActionPolicy.MouseButtonFlags("left"));
        Assert.AreEqual((0x0002u, 0x0004u), HotkeyActionPolicy.MouseButtonFlags("bogus"), "unknown buttons fall back to left");
    }

    [TestMethod]
    public void WheelAmount_NumbersAndDirections()
    {
        Assert.AreEqual(120, HotkeyActionPolicy.WheelAmount("120"));
        Assert.AreEqual(-120, HotkeyActionPolicy.WheelAmount("down"));
        Assert.AreEqual(-120, HotkeyActionPolicy.WheelAmount("DOWN"));
        Assert.AreEqual(120, HotkeyActionPolicy.WheelAmount("up"));
        Assert.AreEqual(120, HotkeyActionPolicy.WheelAmount("anything else"));
    }
}
