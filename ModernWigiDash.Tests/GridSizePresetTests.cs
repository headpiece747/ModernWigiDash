namespace ModernWigiDash.Tests;

[TestClass]
public class GridSizePresetTests
{
    [TestMethod]
    public void GridSizePreset_ToSize_CalculatesCorrectDimensions()
    {
        // The full 20-row table — a wrong row in the mirror must not slip
        // through the 2-row spot check.
        var expected = new Dictionary<GridSizePreset, (float Width, float Height)>
        {
            [GridSizePreset.Size1x1] = (203, 148),
            [GridSizePreset.Size2x1] = (406, 148),
            [GridSizePreset.Size3x1] = (609, 148),
            [GridSizePreset.Size4x1] = (813, 148),
            [GridSizePreset.Size5x1] = (1016, 148),
            [GridSizePreset.Size1x2] = (203, 296),
            [GridSizePreset.Size2x2] = (406, 296),
            [GridSizePreset.Size3x2] = (609, 296),
            [GridSizePreset.Size4x2] = (813, 296),
            [GridSizePreset.Size5x2] = (1016, 296),
            [GridSizePreset.Size1x3] = (203, 444),
            [GridSizePreset.Size2x3] = (406, 444),
            [GridSizePreset.Size3x3] = (609, 444),
            [GridSizePreset.Size4x3] = (813, 444),
            [GridSizePreset.Size5x3] = (1016, 444),
            [GridSizePreset.Size1x4] = (203, 592),
            [GridSizePreset.Size2x4] = (406, 592),
            [GridSizePreset.Size3x4] = (609, 592),
            [GridSizePreset.Size4x4] = (813, 592),
            [GridSizePreset.Size5x4] = (1016, 592),
        };

        foreach ((GridSizePreset preset, var (width, height)) in expected)
        {
            SKSize size = preset.ToSize();
            Assert.AreEqual(width, size.Width, $"{preset} width");
            Assert.AreEqual(height, size.Height, $"{preset} height");
        }
    }

    [TestMethod]
    public void SnapToCell_RoundsToNearestCellBoundary()
    {
        Assert.AreEqual(0f, GridSizeExtensions.SnapX(0f), 0.001f);
        Assert.AreEqual(0f, GridSizeExtensions.SnapX(100f), 0.001f, "below the half-cell midpoint rounds down");
        Assert.AreEqual(203.2f, GridSizeExtensions.SnapX(150f), 0.001f, "past the midpoint rounds up");
        Assert.AreEqual(203.2f, GridSizeExtensions.SnapX(203.2f), 0.001f, "exact boundaries stay put");
        Assert.AreEqual(406.4f, GridSizeExtensions.SnapX(400f), 0.001f);
        Assert.AreEqual(-203.2f, GridSizeExtensions.SnapX(-150f), 0.001f, "negatives mirror");
    }

    [TestMethod]
    public void SnapToCell_YAxisUsesCellHeight()
    {
        Assert.AreEqual(0f, GridSizeExtensions.SnapY(74f), 0.001f);
        Assert.AreEqual(148f, GridSizeExtensions.SnapY(100f), 0.001f);
    }

    [TestMethod]
    public void FrameTimeDisplay_IsCompact_FollowsTheHeightBreakpoint()
    {
        var dto = new FrameTimeSnapshotDto { IsAvailable = true, CaptureHealthy = true, ProcessId = 4321, RecentFrameTimesMs = [1, 2] };

        var compact = FrameTimePresentation.Build(dto, new SKSize(600f, 100f));
        var full = FrameTimePresentation.Build(dto, new SKSize(600f, 200f));

        Assert.IsTrue(compact.IsCompact, "under 150px is the compact layout");
        Assert.IsFalse(full.IsCompact);
    }
}
