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
}
