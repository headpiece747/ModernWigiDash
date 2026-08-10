using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class GridSizePresetTests
{
    [TestMethod]
    public void GridSizePreset_ToSize_CalculatesCorrectDimensions()
    {
        SKSize size2x2 = GridSizePreset.Size2x2.ToSize();
        Assert.AreEqual(406f, size2x2.Width);
        Assert.AreEqual(296f, size2x2.Height);

        SKSize size5x4 = GridSizePreset.Size5x4.ToSize();
        Assert.AreEqual(1016f, size5x4.Width);
        Assert.AreEqual(592f, size5x4.Height);
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
}
