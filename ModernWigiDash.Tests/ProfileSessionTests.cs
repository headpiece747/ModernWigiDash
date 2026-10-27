using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.Tests;

[TestClass]
public class ProfileSessionTests
{
    private sealed class FakePageDialogs : IPageDialogs
    {
        public bool ConfirmResponse { get; set; } = true;
        public string? PromptResponse { get; set; } = "Renamed Page";

        public bool Confirm(string title, string message) => ConfirmResponse;
        public string? PromptForText(string title, string label, string initialValue) => PromptResponse;
    }

    private static (ProfileSession session, List<(ProfileMutationShape shape, PlacedWidgetInstance? selection)> mutations, ProfileLayout profile) CreateSession(
        ProfileLayout? initialProfile = null,
        FakePageDialogs? dialogs = null)
    {
        var profile = initialProfile ?? new ProfileLayout
        {
            Pages =
            [
                new PageLayout { PageName = "Page 1" },
                new PageLayout { PageName = "Page 2" }
            ],
            ActivePageIndex = 0
        };

        var mutations = new List<(ProfileMutationShape shape, PlacedWidgetInstance? selection)>();
        var recordingFunnel = new RecordingMutationFunnel(mutations);

        var pageDialogs = dialogs ?? new FakePageDialogs();
        var pageManagement = new PageManagement(() => profile, pageDialogs);
        var loader = new WidgetPluginLoader();
        var context = new TestContext();
        PlacedWidgetInstance? currentSelection = null;

        var session = new ProfileSession(
            () => profile,
            recordingFunnel,
            pageManagement,
            loader,
            context,
            () => currentSelection);

        return (session, mutations, profile);
    }

    private sealed class RecordingMutationFunnel(List<(ProfileMutationShape shape, PlacedWidgetInstance? selection)> records)
        : IProfileMutationFunnel
    {
        public void Apply(ProfileMutationShape shape, ProfileLayout profile, PlacedWidgetInstance? selection)
        {
            records.Add((shape, selection));
        }
    }

    [TestMethod]
    public void AddPage_AddsAndActivates_AndAppliesStructuralMutation()
    {
        var (session, mutations, profile) = CreateSession();

        var verdict = session.AddPage();

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual(3, profile.Pages.Count);
        Assert.AreEqual(2, profile.ActivePageIndex);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Structural, mutations[0].shape);
        Assert.IsNull(mutations[0].selection);
    }

    [TestMethod]
    public void DeletePage_Allowed_DeletesPage_AndAppliesStructuralMutation()
    {
        var (session, mutations, profile) = CreateSession();

        var verdict = session.DeletePage(0);

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual(1, profile.Pages.Count);
        Assert.AreEqual(0, profile.ActivePageIndex);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Structural, mutations[0].shape);
    }

    [TestMethod]
    public void DeletePage_LastPage_Refuses_AndDoesNotMutate()
    {
        var singlePageProfile = new ProfileLayout
        {
            Pages = [new PageLayout { PageName = "Only Page" }],
            ActivePageIndex = 0
        };
        var (session, mutations, profile) = CreateSession(singlePageProfile);

        var verdict = session.DeletePage(0);

        Assert.AreEqual(PageOpVerdict.LastPage, verdict);
        Assert.AreEqual(1, profile.Pages.Count);
        Assert.AreEqual(0, mutations.Count);
    }

    [TestMethod]
    public void RenamePage_ValidName_RenamesPage_AndAppliesStructuralMutation()
    {
        var dialogs = new FakePageDialogs { PromptResponse = "Gaming Hub" };
        var (session, mutations, profile) = CreateSession(dialogs: dialogs);

        var verdict = session.RenamePage(0);

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual("Gaming Hub", profile.Pages[0].PageName);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Structural, mutations[0].shape);
    }

    [TestMethod]
    public void RenamePage_BlankName_Refuses_AndDoesNotMutate()
    {
        var dialogs = new FakePageDialogs { PromptResponse = "   " };
        var (session, mutations, profile) = CreateSession(dialogs: dialogs);

        var verdict = session.RenamePage(0);

        Assert.AreEqual(PageOpVerdict.BlankName, verdict);
        Assert.AreEqual("Page 1", profile.Pages[0].PageName);
        Assert.AreEqual(0, mutations.Count);
    }

    [TestMethod]
    public void SwitchToPage_ValidIndex_SwitchesActivePage_AndAppliesStructuralMutation()
    {
        var (session, mutations, profile) = CreateSession();

        var verdict = session.SwitchToPage(1);

        Assert.AreEqual(PageOpVerdict.Applied, verdict);
        Assert.AreEqual(1, profile.ActivePageIndex);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Structural, mutations[0].shape);
    }

    [TestMethod]
    public void SwitchToPage_InvalidIndex_Refuses_AndDoesNotMutate()
    {
        var (session, mutations, profile) = CreateSession();

        var verdict = session.SwitchToPage(99);

        Assert.AreEqual(PageOpVerdict.StaleIndex, verdict);
        Assert.AreEqual(0, profile.ActivePageIndex);
        Assert.AreEqual(0, mutations.Count);
    }

    [TestMethod]
    public void SetSnapToGrid_UpdatesActivePage_AndAppliesTransformMutation()
    {
        var (session, mutations, profile) = CreateSession();
        profile.ActivePage.SnapToGrid = false;

        session.SetSnapToGrid(true);

        Assert.IsTrue(profile.ActivePage.SnapToGrid);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Transform, mutations[0].shape);
    }

    [TestMethod]
    public void SetPageBackground_UpdatesActivePage_AndAppliesTransformMutation()
    {
        var (session, mutations, profile) = CreateSession();

        session.SetPageBackground("#FF112233");

        Assert.AreEqual("#FF112233", profile.ActivePage.BackgroundHexColor);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Transform, mutations[0].shape);
    }

    [TestMethod]
    public void RemoveWidget_ExistingWidget_RemovesFromPage_AndAppliesTransformMutation()
    {
        var (session, mutations, profile) = CreateSession();
        var placed = new PlacedWidgetInstance
        {
            PluginId = "test",
            X = 0,
            Y = 0,
            Width = 100,
            Height = 100
        };
        profile.ActivePage.Widgets.Add(placed);

        bool removed = session.RemoveWidget(placed);

        Assert.IsTrue(removed);
        Assert.AreEqual(0, profile.ActivePage.Widgets.Count);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Transform, mutations[0].shape);
    }

    [TestMethod]
    public void RemoveWidget_NonExistingWidget_ReturnsFalse_AndDoesNotMutate()
    {
        var (session, mutations, _) = CreateSession();
        var placed = new PlacedWidgetInstance { PluginId = "test" };

        bool removed = session.RemoveWidget(placed);

        Assert.IsFalse(removed);
        Assert.AreEqual(0, mutations.Count);
    }

    [TestMethod]
    public void ClearActivePage_WithWidgets_ClearsPage_AndAppliesTransformMutation()
    {
        var (session, mutations, profile) = CreateSession();
        profile.ActivePage.Widgets.Add(new PlacedWidgetInstance { PluginId = "w1" });
        profile.ActivePage.Widgets.Add(new PlacedWidgetInstance { PluginId = "w2" });

        bool cleared = session.ClearActivePage();

        Assert.IsTrue(cleared);
        Assert.AreEqual(0, profile.ActivePage.Widgets.Count);
        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Transform, mutations[0].shape);
    }

    [TestMethod]
    public void ClearActivePage_EmptyPage_ReturnsFalse_AndDoesNotMutate()
    {
        var (session, mutations, _) = CreateSession();

        bool cleared = session.ClearActivePage();

        Assert.IsFalse(cleared);
        Assert.AreEqual(0, mutations.Count);
    }

    [TestMethod]
    public void ApplyRawWrite_AppliesRawWriteMutation()
    {
        var (session, mutations, _) = CreateSession();

        session.ApplyRawWrite();

        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.RawWrite, mutations[0].shape);
    }

    [TestMethod]
    public void CommitTransform_AppliesTransformMutation()
    {
        var (session, mutations, _) = CreateSession();

        session.CommitTransform();

        Assert.AreEqual(1, mutations.Count);
        Assert.AreEqual(ProfileMutationShape.Transform, mutations[0].shape);
    }
}
