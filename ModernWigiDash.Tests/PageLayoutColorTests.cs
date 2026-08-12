using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.Tests;

[TestClass]
public class PageLayoutColorTests
{
    [TestMethod]
    public void BackgroundHexColor_SetsNormalizedValue()
    {
        var page = new PageLayout { BackgroundHexColor = " #A1B2C3 " };
        Assert.AreEqual("#A1B2C3", page.BackgroundHexColor);
    }

    [TestMethod]
    public void BackgroundHexColor_Empty_FallsBackToDefault()
    {
        var page = new PageLayout { BackgroundHexColor = "   " };
        Assert.AreEqual(PageLayout.DefaultBackgroundHexColor, page.BackgroundHexColor);
    }

    [TestMethod]
    public void BackgroundHexColor_ExportImport_RoundTrips()
    {
        var profile = new ProfileLayout();
        profile.Pages[0].BackgroundHexColor = "#F59E0B";

        var json = ProfileOps.ExportJson(profile);
        var imported = ProfileOps.ImportJson(json, new WidgetPluginLoader(), new TestContext());

        Assert.AreEqual("#F59E0B", imported!.Pages[0].BackgroundHexColor);
    }
}
