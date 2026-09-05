namespace ModernWigiDash.Tests;

/// <summary>
/// The page-management module pinned at its interface without a window: the
/// add/delete/rename/switch gate rules (the last-page veto, the stale-index
/// no-op, the confirm-cancel, the blank-rename no-op) and the verdict the
/// window's funnel reads. The dialog seam is an in-memory fake, so the gate
/// rules are assertable without a window, an STA thread, or a real
/// DialogHost.
/// </summary>
[TestClass]
public class PageManagementTests
{
    private sealed class FakeDialogs : IPageDialogs
    {
        public bool ConfirmResult = true;
        public string? PromptResult = "New Name";
        public int ConfirmCalls;
        public int PromptCalls;

        public bool Confirm(string title, string message)
        {
            ConfirmCalls++;
            return ConfirmResult;
        }

        public string? PromptForText(string title, string label, string initialValue)
        {
            PromptCalls++;
            return PromptResult;
        }
    }

    private static ProfileLayout TwoPages()
    {
        // A fresh profile already carries one default page; add a second.
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile);
        return profile;
    }

    [TestMethod]
    public void Add_AlwaysApplies_AndActivatesTheNewPage()
    {
        var profile = TwoPages();
        var module = new PageManagement(() => profile, new FakeDialogs());

        var verdict = module.Add();

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual(3, profile.Pages.Count);
        Assert.AreEqual(2, profile.ActivePageIndex);
    }

    [TestMethod]
    public void CanDelete_SinglePage_False_MultiplePages_True()
    {
        var single = new ProfileLayout(); // one default page
        Assert.IsFalse(new PageManagement(() => single, new FakeDialogs()).CanDelete());

        var multi = TwoPages();
        Assert.IsTrue(new PageManagement(() => multi, new FakeDialogs()).CanDelete());
    }

    [TestMethod]
    public void Delete_LastPage_IsRefused_WithoutConfirming()
    {
        var single = new ProfileLayout(); // one default page
        var dialogs = new FakeDialogs();
        var module = new PageManagement(() => single, dialogs);

        var verdict = module.Delete(0);

        Assert.AreEqual(PageOpVerdict.LastPage, verdict);
        Assert.AreEqual(1, single.Pages.Count);
        Assert.AreEqual(0, dialogs.ConfirmCalls, "the last-page veto precedes the confirm");
    }

    [TestMethod]
    public void Delete_StaleIndex_IsANoOpNotAThrow()
    {
        var profile = TwoPages();
        var module = new PageManagement(() => profile, new FakeDialogs());

        Assert.AreEqual(PageOpVerdict.StaleIndex, module.Delete(99));
        Assert.AreEqual(PageOpVerdict.StaleIndex, module.Delete(-1));
        Assert.AreEqual(2, profile.Pages.Count);
    }

    [TestMethod]
    public void Delete_ConfirmCancelled_IsCancelled_NoDelete()
    {
        var profile = TwoPages();
        // A widget on the target page makes the delete ask for confirmation.
        profile.Pages[1].Widgets.Add(new PlacedWidgetInstance());
        var dialogs = new FakeDialogs { ConfirmResult = false };
        var module = new PageManagement(() => profile, dialogs);

        var verdict = module.Delete(1);

        Assert.AreEqual(PageOpVerdict.Cancelled, verdict);
        Assert.AreEqual(2, profile.Pages.Count);
        Assert.AreEqual(1, dialogs.ConfirmCalls);
    }

    [TestMethod]
    public void Delete_EmptyPage_Applies_WithoutConfirming()
    {
        var profile = TwoPages();
        var dialogs = new FakeDialogs();
        var module = new PageManagement(() => profile, dialogs);

        var verdict = module.Delete(1);

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual(1, profile.Pages.Count);
        Assert.AreEqual(0, dialogs.ConfirmCalls, "an empty page deletes without the confirm");
    }

    [TestMethod]
    public void Rename_BlankPrompt_IsBlankName_NoRename()
    {
        var profile = TwoPages();
        var before = profile.Pages[0].PageName;
        var dialogs = new FakeDialogs { PromptResult = "   " };
        var module = new PageManagement(() => profile, dialogs);

        var verdict = module.Rename(0);

        Assert.AreEqual(PageOpVerdict.BlankName, verdict);
        Assert.AreEqual(before, profile.Pages[0].PageName);
    }

    [TestMethod]
    public void Rename_ValidPrompt_RenamesAndApplies()
    {
        var profile = TwoPages();
        var dialogs = new FakeDialogs { PromptResult = "Renamed" };
        var module = new PageManagement(() => profile, dialogs);

        var verdict = module.Rename(0);

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual("Renamed", profile.Pages[0].PageName);
        Assert.AreEqual(1, dialogs.PromptCalls);
    }

    [TestMethod]
    public void Switch_OutOfRange_IsStale_NoSwitch()
    {
        var profile = TwoPages();
        var module = new PageManagement(() => profile, new FakeDialogs());

        Assert.AreEqual(PageOpVerdict.StaleIndex, module.Switch(5));
        Assert.AreEqual(PageOpVerdict.StaleIndex, module.Switch(-1));
    }

    [TestMethod]
    public void Switch_InRange_SwitchesAndApplies()
    {
        var profile = TwoPages();
        var module = new PageManagement(() => profile, new FakeDialogs());

        var verdict = module.Switch(0);

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual(0, profile.ActivePageIndex);
    }
}
