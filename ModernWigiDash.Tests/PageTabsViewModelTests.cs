using ModernWigiDash.App;
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Tests;

/// <summary>
/// The pure page-tab rules that RebuildPageTabsUI renders — active tab,
/// delete-only-when-more-than-one-page, labels.
/// </summary>
[TestClass]
public class PageTabsViewModelTests
{
    [TestMethod]
    public void Build_SinglePage_ActiveAndNotDeletable()
    {
        var profile = new ProfileLayout();

        var tabs = PageTabsViewModel.Build(profile);

        Assert.AreEqual(1, tabs.Count);
        Assert.IsTrue(tabs[0].IsActive);
        Assert.IsFalse(tabs[0].CanDelete, "The last remaining page must never be deletable");
    }

    [TestMethod]
    public void Build_MultiplePages_MarksActiveAndDeletable()
    {
        var profile = new ProfileLayout(); // starts with "Main Dashboard"
        ProfileOps.AddPage(profile, "A");
        ProfileOps.AddPage(profile, "B");
        profile.ActivePageIndex = 1;

        var tabs = PageTabsViewModel.Build(profile);

        Assert.AreEqual(3, tabs.Count);
        Assert.IsTrue(tabs.All(t => t.CanDelete), "With more than one page every tab is deletable");
        Assert.IsFalse(tabs[0].IsActive);
        Assert.IsTrue(tabs[1].IsActive);
        Assert.AreEqual("A", tabs[1].PageName);
        Assert.AreEqual(1, tabs[1].Index);
    }

    [TestMethod]
    public void Build_TabIndexes_MatchPageOrder()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "One");
        ProfileOps.AddPage(profile, "Two");

        var tabs = PageTabsViewModel.Build(profile);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, tabs.Select(t => t.Index).ToArray());
        CollectionAssert.AreEqual(profile.Pages.Select(p => p.PageName).ToArray(), tabs.Select(t => t.PageName).ToArray());
    }

    [TestMethod]
    public void CanDelete_SinglePage_IsFalse()
    {
        var profile = new ProfileLayout();

        Assert.IsFalse(PageTabsViewModel.CanDelete(profile),
            "The last remaining page must never be deletable");
    }

    [TestMethod]
    public void CanDelete_MultiplePages_IsTrue()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "A");

        Assert.IsTrue(PageTabsViewModel.CanDelete(profile));
    }

    [TestMethod]
    public void CanDelete_MatchesBuild_TabRulesShareOneRule()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "A");

        var tabs = PageTabsViewModel.Build(profile);

        Assert.IsTrue(tabs.All(t => t.CanDelete == PageTabsViewModel.CanDelete(profile)),
            "the tab strip must consume the same rule the window's delete flow uses");
    }
}
