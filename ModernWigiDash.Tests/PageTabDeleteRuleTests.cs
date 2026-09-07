namespace ModernWigiDash.Tests;

/// <summary>
/// The page-tab delete rule that <c>PageTabsView.Rebuild</c> derives per tab:
/// the last remaining page can never be deleted. The rule lives in
/// <see cref="ProfileOps.CanDeletePage"/> beside DeletePage, so the tab strip
/// and the delete operation share one owner.
/// </summary>
[TestClass]
public class PageTabDeleteRuleTests
{
    [TestMethod]
    public void CanDeletePage_SinglePage_IsFalse()
    {
        var profile = new ProfileLayout();

        Assert.IsFalse(ProfileOps.CanDeletePage(profile),
            "The last remaining page must never be deletable");
    }

    [TestMethod]
    public void CanDeletePage_MultiplePages_IsTrue()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "A");

        Assert.IsTrue(ProfileOps.CanDeletePage(profile));
    }
}
