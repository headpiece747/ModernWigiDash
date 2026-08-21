using System.Windows;

namespace ModernWigiDash.Tests;

/// <summary>
/// The pure page-tab layout rules that PageTabsView bakes into its buttons —
/// the padding/margin/geometry constants, pinned per delete state.
/// </summary>
[TestClass]
public class PageTabVisualTests
{
    private static PageTabVisual Visual(bool isActive, bool canDelete)
        => new(new PageTabItem("Tab", 0, isActive, canDelete));

    [TestMethod]
    public void TabPadding_WithDelete_LeavesRoomForBothIconButtons()
    {
        var visual = Visual(isActive: false, canDelete: true);

        Assert.AreEqual(new Thickness(14, 6, 56, 6), visual.TabPadding);
    }

    [TestMethod]
    public void TabPadding_WithoutDelete_UsesSmallerRightInset()
    {
        var visual = Visual(isActive: false, canDelete: false);

        Assert.AreEqual(new Thickness(14, 6, 42, 6), visual.TabPadding);
    }

    [TestMethod]
    public void RenameIconMargin_WithDelete_ClearsTheCloseButton()
    {
        var visual = Visual(isActive: false, canDelete: true);

        Assert.AreEqual(new Thickness(0, 0, 24, 0), visual.RenameIconMargin);
    }

    [TestMethod]
    public void RenameIconMargin_WithoutDelete_SitsSnugAtTheTabEdge()
    {
        var visual = Visual(isActive: false, canDelete: false);

        Assert.AreEqual(new Thickness(0, 0, 4, 0), visual.RenameIconMargin);
    }

    [TestMethod]
    public void CloseIconMargin_IsSnugAtTheStripEdge()
    {
        var visual = Visual(isActive: false, canDelete: true);

        Assert.AreEqual(new Thickness(0, 0, 4, 0), visual.CloseIconMargin);
    }

    [TestMethod]
    public void IconGeometry_IsSharedByRenameAndCloseButtons()
    {
        Assert.AreEqual(20, PageTabVisual.IconSize);
        Assert.AreEqual(10, PageTabVisual.IconFontSize);
    }

    [TestMethod]
    public void CanDelete_FlowsFromTheItem_WhenDeleteAllowed()
    {
        var visual = Visual(isActive: true, canDelete: true);

        Assert.IsTrue(visual.CanDelete);
        Assert.IsTrue(visual.IsActive);
    }
}
