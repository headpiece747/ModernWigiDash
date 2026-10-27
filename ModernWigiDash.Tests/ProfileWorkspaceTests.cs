using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWigiDash.App;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class ProfileWorkspaceTests
{
    private sealed class TestFunnel : IProfileMutationFunnel
    {
        public int CallCount { get; private set; }
        public ProfileMutationShape LastShape { get; private set; }
        public PlacedWidgetInstance? LastSelection { get; private set; }

        public void Apply(ProfileMutationShape shape, ProfileLayout profile, PlacedWidgetInstance? selection)
        {
            CallCount++;
            LastShape = shape;
            LastSelection = selection;
        }
    }

    private sealed class TestExportHost(Func<ProfileLayout> profile) : IProfileExportHost
    {
        public ProfileLayout Profile => profile();
        public ThemeSettings CurrentTheme => new();
        public void ShowSuccess(string title, string message) { }
        public void ShowError(string title, string message) { }
    }

    private static (ProfileWorkspace Workspace, TestFunnel Funnel, ProfileLayout Profile, string ProfilePath) CreateWorkspace(ProfileLayout? initial = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wmd-pw-{Guid.NewGuid():N}", "profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var profile = initial ?? new ProfileLayout
        {
            ProfileId = "test-prof",
            Pages = [new PageLayout { PageName = "Page 1" }, new PageLayout { PageName = "Page 2" }]
        };

        var funnel = new TestFunnel();
        var pageMgmt = new PageManagement(() => profile, new PageManagement.DialogHostAdapter(new DialogHost(null!, null!, _ => null, (_, _) => { })));
        var loader = new WidgetPluginLoader();
        var context = new TestContext();
        PlacedWidgetInstance? selected = null;

        var persistence = new ProfilePersistence(path, () => profile, debounceDelay: TimeSpan.FromDays(1));
        var session = new ProfileSession(() => profile, funnel, pageMgmt, loader, context, () => selected);
        var adapter = new ProfileImportAdapter(
            () => profile,
            p => profile = p,
            session.ApplyRawWrite,
            () => new DialogHost(null!, null!, _ => null, (_, _) => { }),
            new ThemeApplicator(),
            null!);

        var ws = new ProfileWorkspace(
            persistence,
            session,
            adapter,
            () => profile,
            () => new DialogHost(null!, null!, _ => null, (_, _) => { }),
            loader,
            context);

        return (ws, funnel, profile, path);
    }

    [TestMethod]
    public void ActiveProperties_ReflectProfileState()
    {
        var (ws, _, profile, path) = CreateWorkspace();
        try
        {
            Assert.AreSame(profile, ws.ActiveProfile);
            Assert.AreEqual("Page 1", ws.ActivePage.PageName);
            Assert.AreEqual(0, ws.ActivePageIndex);
            Assert.IsTrue(ws.CanDeletePage, "Two pages: page can be deleted");
        }
        finally
        {
            ws.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void SetSnapToGrid_And_SetPageBackground_MutatePageAndFunnel()
    {
        var (ws, funnel, _, path) = CreateWorkspace();
        try
        {
            ws.SetSnapToGrid(true);
            Assert.IsTrue(ws.ActivePage.SnapToGrid);
            Assert.AreEqual(ProfileMutationShape.Transform, funnel.LastShape);

            ws.SetPageBackground("#FF00FF");
            Assert.AreEqual("#FF00FF", ws.ActivePage.BackgroundHexColor);
            Assert.AreEqual(2, funnel.CallCount);
        }
        finally
        {
            ws.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void AddPage_DeletePage_SwitchToPage_UpdateProfile()
    {
        var (ws, funnel, profile, path) = CreateWorkspace();
        try
        {
            var addVerdict = ws.AddPage();
            Assert.AreEqual(PageOpVerdict.Applied, addVerdict);
            Assert.AreEqual(3, profile.Pages.Count);
            Assert.AreEqual(ProfileMutationShape.Structural, funnel.LastShape);

            var switchVerdict = ws.SwitchToPage(2);
            Assert.AreEqual(PageOpVerdict.Applied, switchVerdict);
            Assert.AreEqual(2, ws.ActivePageIndex);

            var deleteVerdict = ws.DeletePage(2);
            Assert.AreEqual(PageOpVerdict.Applied, deleteVerdict);
            Assert.AreEqual(2, profile.Pages.Count);
        }
        finally
        {
            ws.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ClearActivePage_EmptiesWidgets_AndNotifiesFunnel()
    {
        var (ws, funnel, profile, path) = CreateWorkspace();
        try
        {
            profile.ActivePage.Widgets.Add(new PlacedWidgetInstance { PluginId = "clock" });
            Assert.AreEqual(1, profile.ActivePage.Widgets.Count);

            bool cleared = ws.ClearActivePage();
            Assert.IsTrue(cleared);
            Assert.AreEqual(0, profile.ActivePage.Widgets.Count);
            Assert.AreEqual(ProfileMutationShape.Transform, funnel.LastShape);
        }
        finally
        {
            ws.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ExportToFile_CreatesJsonWithTheme()
    {
        var (ws, _, _, path) = CreateWorkspace();
        string exportFile = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.json");
        try
        {
            var outcome = ws.ExportToFile(exportFile, new TestExportHost(() => ws.ActiveProfile));
            Assert.IsInstanceOfType(outcome, typeof(ProfileExportFlowOutcome.Exported));
            Assert.IsTrue(File.Exists(exportFile));

            string content = File.ReadAllText(exportFile);
            Assert.IsTrue(content.Contains("test-prof"));
        }
        finally
        {
            ws.Dispose();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(exportFile)) File.Delete(exportFile);
        }
    }

    [TestMethod]
    public void SwapProfile_UpdatesActiveProfile_AndNotifiesFunnel()
    {
        var (ws, funnel, _, path) = CreateWorkspace();
        try
        {
            var newProfile = new ProfileLayout
            {
                ProfileId = "swapped-profile",
                Pages = [new PageLayout { PageName = "Swapped Page" }]
            };

            bool swapped = ws.SwapProfile(newProfile);
            Assert.IsTrue(swapped);
            Assert.AreEqual("swapped-profile", ws.ActiveProfile.ProfileId);
            Assert.AreEqual(ProfileMutationShape.RawWrite, funnel.LastShape);
        }
        finally
        {
            ws.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
