using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

[TestClass]
public class StarterProfileTests
{
    private const float CanvasWidth = 1016f;
    private const float CanvasHeight = 592f;

    private static readonly string[] ExpectedPageNames =
    [
        "Main Dashboard", "Now Playing", "Weather Forecast",
        "Twitch & Picture", "Hardware Monitor", "FPS / Frame Time"
    ];

    // ── layout spec (pure data — no widget instantiation, no loader/context) ──

    [TestMethod]
    public void Layout_DefinesSixPagesInExpectedOrder()
    {
        CollectionAssert.AreEqual(ExpectedPageNames, StarterProfile.Layout.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void Layout_EveryPlacementHasNonEmptyPluginId()
    {
        Assert.IsTrue(StarterProfile.Layout.Count > 0);
        foreach (var page in StarterProfile.Layout)
        {
            foreach (var placement in page.Placements)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(placement.PluginId),
                    $"{page.Name}: plugin id must not be empty");
            }
        }
    }

    [TestMethod]
    public void Layout_AllPlacementsInBoundsWithPositiveSize()
    {
        foreach (var page in StarterProfile.Layout)
        {
            foreach (var placement in page.Placements)
            {
                string context = $"{page.Name}/{placement.PluginId}";
                Assert.IsTrue(placement.X >= 0, $"{context}: X must be non-negative (got {placement.X})");
                Assert.IsTrue(placement.Y >= 0, $"{context}: Y must be non-negative (got {placement.Y})");
                Assert.IsTrue(placement.Width > 0, $"{context}: width must be positive (got {placement.Width})");
                Assert.IsTrue(placement.Height > 0, $"{context}: height must be positive (got {placement.Height})");
                Assert.IsTrue(placement.X + placement.Width <= CanvasWidth,
                    $"{context}: right edge {placement.X + placement.Width} exceeds canvas width {CanvasWidth}");
                Assert.IsTrue(placement.Y + placement.Height <= CanvasHeight,
                    $"{context}: bottom edge {placement.Y + placement.Height} exceeds canvas height {CanvasHeight}");
            }
        }
    }

    [TestMethod]
    public void MainDashboardPage_ContainsEightDashboardPlacements()
    {
        var dashboard = StarterProfile.Layout[0];
        Assert.AreEqual("Main Dashboard", dashboard.Name);
        Assert.AreEqual(8, dashboard.Placements.Count);

        CollectionAssert.AreEqual(
            new[]
            {
                "clock_modern", "weather_forecast", "audio_visualizer", "frame_time",
                "ticker_stock", "text_label", "hotkey_button", "stopwatch_timer"
            },
            dashboard.Placements.Select(p => p.PluginId).ToArray());
    }
}
