using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>The update button's state → presentation table — pinned without WPF,
/// mirroring the UsbBadgeModel tests.</summary>
[TestClass]
public class UpdateBadgeModelTests
{
    [TestMethod]
    public void From_Available_ArrowIconAmberVisible()
    {
        var badge = UpdateBadgeModel.From(UpdateState.Available);

        Assert.AreEqual("arrow-circle-down", badge.IconName);
        Assert.AreEqual((245, 158, 11), (badge.Red, badge.Green, badge.Blue));
        Assert.IsTrue(badge.IsVisible);
    }

    [TestMethod]
    public void From_Downloading_SwapIconWhiteVisible()
    {
        var badge = UpdateBadgeModel.From(UpdateState.Downloading);

        Assert.AreEqual("swap-horizontal", badge.IconName);
        Assert.AreEqual((250, 250, 250), (badge.Red, badge.Green, badge.Blue));
        Assert.IsTrue(badge.IsVisible);
    }

    [TestMethod]
    public void From_Ready_RefreshIconGreenVisible()
    {
        var badge = UpdateBadgeModel.From(UpdateState.Ready);

        Assert.AreEqual("refresh", badge.IconName);
        Assert.AreEqual((16, 185, 129), (badge.Red, badge.Green, badge.Blue));
        Assert.IsTrue(badge.IsVisible);
    }

    [TestMethod]
    public void From_Hidden_NoIconInvisible()
    {
        var badge = UpdateBadgeModel.From(UpdateState.Hidden);

        Assert.AreEqual("", badge.IconName);
        Assert.IsFalse(badge.IsVisible);
    }
}
