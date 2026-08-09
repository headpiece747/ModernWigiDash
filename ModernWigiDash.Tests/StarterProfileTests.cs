using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class StarterProfileTests
{
    private sealed class FakeContext : IModernWigiDashContext
    {
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void RequestRender() { }
        public void RequestInspectorRefresh() { }
        public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) { }
        public void CloseDeviceAuthorization() { }
    }

    private static WidgetPluginLoader CreateLoader()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInAssembly(typeof(HotkeyButtonWidget).Assembly);
        return loader;
    }

    private static ProfileLayout CreateProfile()
    {
        var profile = new StarterProfile(CreateLoader(), new FakeContext()).Create();
        try
        {
            return profile;
        }
        catch
        {
            ProfileOps.DisposeProfile(profile);
            throw;
        }
    }

    // ── placement table (pure data) ─────────────────────────

    [TestMethod]
    public void PlacementTable_DefinesSixPagesInOrder()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "Main Dashboard", "Now Playing", "Weather Forecast",
                "Twitch & Picture", "Hardware Monitor", "FPS / Frame Time"
            },
            StarterProfile.PageNames.ToArray());

        Assert.AreEqual(6, StarterProfile.Placements.Select(p => p.PageName).Distinct().Count());
        Assert.IsTrue(StarterProfile.Placements.All(p => StarterProfile.PageNames.Contains(p.PageName)),
            "Every placement must target one of the declared pages");
    }

    [TestMethod]
    public void PlacementTable_AllPlacementsInBoundsWithPositiveSize()
    {
        Assert.IsTrue(StarterProfile.Placements.Count > 0);
        foreach (var placement in StarterProfile.Placements)
        {
            Assert.IsTrue(placement.X >= 0, $"{placement.PluginId}: X must be non-negative (got {placement.X})");
            Assert.IsTrue(placement.Y >= 0, $"{placement.PluginId}: Y must be non-negative (got {placement.Y})");
            Assert.IsTrue(placement.Width > 0, $"{placement.PluginId}: width must be positive (got {placement.Width})");
            Assert.IsTrue(placement.Height > 0, $"{placement.PluginId}: height must be positive (got {placement.Height})");
            Assert.IsTrue(placement.X + placement.Width <= GridSizeExtensions.ScreenWidth,
                $"{placement.PluginId}: right edge {placement.X + placement.Width} exceeds canvas width {GridSizeExtensions.ScreenWidth}");
            Assert.IsTrue(placement.Y + placement.Height <= GridSizeExtensions.ScreenHeight,
                $"{placement.PluginId}: bottom edge {placement.Y + placement.Height} exceeds canvas height {GridSizeExtensions.ScreenHeight}");
        }
    }

    // ── Create() (rehydrates through the real widget assembly) ──

    [TestMethod]
    public void Create_BuildsSixPagesInOrderWithExpectedNames()
    {
        var profile = CreateProfile();
        try
        {
            Assert.AreEqual(6, profile.Pages.Count);
            CollectionAssert.AreEqual(StarterProfile.PageNames.ToArray(), profile.Pages.Select(p => p.PageName).ToArray());
            Assert.AreEqual(0, profile.ActivePageIndex, "Starter profile must open on the first page");
            Assert.IsNotNull(profile.ActivePage);
        }
        finally
        {
            ProfileOps.DisposeProfile(profile);
        }
    }

    [TestMethod]
    public void Create_RehydratesEveryPlacementInBounds()
    {
        var profile = CreateProfile();
        try
        {
            int total = profile.Pages.Sum(page => page.Widgets.Count);
            Assert.AreEqual(StarterProfile.Placements.Count, total,
                "Every placement must rehydrate into a placed widget (unknown plugin ids would be skipped)");

            foreach (var page in profile.Pages)
            {
                foreach (var placed in page.Widgets)
                {
                    Assert.IsTrue(placed.X >= 0, $"{placed.PluginId}: X must be non-negative (got {placed.X})");
                    Assert.IsTrue(placed.Y >= 0, $"{placed.PluginId}: Y must be non-negative (got {placed.Y})");
                    Assert.IsTrue(placed.Width > 0, $"{placed.PluginId}: width must be positive (got {placed.Width})");
                    Assert.IsTrue(placed.Height > 0, $"{placed.PluginId}: height must be positive (got {placed.Height})");
                    Assert.IsTrue(placed.X + placed.Width <= GridSizeExtensions.ScreenWidth,
                        $"{placed.PluginId}: right edge {placed.X + placed.Width} exceeds canvas width {GridSizeExtensions.ScreenWidth}");
                    Assert.IsTrue(placed.Y + placed.Height <= GridSizeExtensions.ScreenHeight,
                        $"{placed.PluginId}: bottom edge {placed.Y + placed.Height} exceeds canvas height {GridSizeExtensions.ScreenHeight}");
                }
            }
        }
        finally
        {
            ProfileOps.DisposeProfile(profile);
        }
    }
}
