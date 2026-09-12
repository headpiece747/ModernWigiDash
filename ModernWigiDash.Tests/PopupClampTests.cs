using System.Windows;
using System.Windows.Controls.Primitives;
using ModernWigiDash.App.Controls;

namespace ModernWigiDash.Tests;

[TestClass]
public class PopupClampTests
{
    [TestMethod]
    public void ComputePlacements_RoomBelow_PrefersBelow()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(200, 300), new Size(100, 30), new Point(20, 40), new Size(1000, 800));

        Assert.AreEqual(new Point(0, 30), placements[0].Point); // directly below the target
        Assert.AreEqual(PopupPrimaryAxis.Horizontal, placements[0].PrimaryAxis);
    }

    [TestMethod]
    public void ComputePlacements_NoRoomBelow_PrefersAbove()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(200, 300), new Size(100, 30), new Point(20, 750), new Size(1000, 800));

        Assert.AreEqual(new Point(0, -300), placements[0].Point); // above the target
    }

    [TestMethod]
    public void ComputePlacements_NoRoomEither_ClampsToClient()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(600, 600), new Size(100, 30), new Point(500, 300), new Size(600, 400));

        var fallback = placements[^1];
        Assert.AreEqual(new Point(-500, -300), fallback.Point); // popup clamped to the client origin, offset relative to the target
    }

    [TestMethod]
    public void ComputePlacements_AlwaysHasFallbackPlacement()
    {
        var placements = PopupClamp.ComputePlacements(
            new Size(200, 100), new Size(50, 20), new Point(10, 10), new Size(300, 200));

        Assert.IsTrue(placements.Length >= 1);
    }

    [TestMethod]
    public void AttachPopupWithinWindow_InvokesTheCustomPlacementCallback()
    {
        // Drive the attached callback on a live window: it resolves the target's
        // client area at placement time and returns the clamped placements. The
        // callback is the one site that reads the window's actual size, so this
        // pins that AttachPopupWithinWindow wires a working callback (not just
        // sets PlacementMode.Custom).
        StaRunner.Run(() =>
        {
            var window = new Window { Width = 800, Height = 600 };
            var grid = new System.Windows.Controls.Grid();
            var target = new System.Windows.Controls.Border { Width = 100, Height = 30 };
            grid.Children.Add(target);
            window.Content = grid;
            window.UpdateLayout();

            var popup = new Popup();
            PopupClamp.AttachPopupWithinWindow(popup, target);

            Assert.AreEqual(PlacementMode.Custom, popup.Placement);
            Assert.IsNotNull(popup.CustomPopupPlacementCallback, "the clamp attaches a callback");

            // Invoke the callback with a popup that fits below the target.
            var placements = popup.CustomPopupPlacementCallback!(new Size(200, 300), new Size(100, 30), new Point(0, 0));
            Assert.IsTrue(placements.Length >= 1, "the callback returns at least the fallback placement");
        });
    }
}
