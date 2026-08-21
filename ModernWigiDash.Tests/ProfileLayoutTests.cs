namespace ModernWigiDash.Tests;

/// <summary>
/// The profile's page-range invariant: Pages is never empty (the setter
/// repairs null/empty to a default page) and the active index is always in
/// range (clamped on write, re-clamped on read) — so ActivePage is total and
/// the old orphan-page fallback (a detached page whose mutations were
/// silently lost) is unrepresentable.
/// </summary>
[TestClass]
public class ProfileLayoutTests
{
    [TestMethod]
    public void Pages_NullOrEmptyAssignment_RepairsToASingleDefaultPage()
    {
        var profile = new ProfileLayout();

        profile.Pages = [];

        Assert.AreEqual(1, profile.Pages.Count, "an empty assignment repairs to the default page");

        profile.Pages = null!;

        Assert.AreEqual(1, profile.Pages.Count, "a null assignment repairs to the default page");
    }

    [TestMethod]
    public void ActivePageIndex_OutOfRangeAssignment_ClampsToThePageRange()
    {
        var profile = new ProfileLayout();
        profile.Pages = [new PageLayout(), new PageLayout(), new PageLayout()];

        profile.ActivePageIndex = 99;
        Assert.AreEqual(2, profile.ActivePageIndex, "an over-range index clamps to the last page");

        profile.ActivePageIndex = -5;
        Assert.AreEqual(0, profile.ActivePageIndex, "a negative index clamps to the first page");
    }

    [TestMethod]
    public void ActivePage_IsAlwaysAMemberOfPages()
    {
        var profile = new ProfileLayout();
        profile.Pages = [new PageLayout(), new PageLayout(), new PageLayout()];
        profile.ActivePageIndex = 1;

        Assert.AreSame(profile.Pages[1], profile.ActivePage,
            "the active page is a member of Pages — the orphan-page fallback is unrepresentable");

        // A list replaced after the index was set: the read-time clamp keeps
        // the page a member instead of fabricating one.
        profile.Pages = [new PageLayout(), new PageLayout()];

        Assert.AreSame(profile.Pages[1], profile.ActivePage,
            "replacing the list re-clamps the stale index at read time");
    }
}
