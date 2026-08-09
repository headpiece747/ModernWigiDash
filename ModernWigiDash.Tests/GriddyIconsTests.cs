using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class GriddyIconsTests
{
    [TestMethod]
    public void GriddyIcons_Names_CountAndUnique()
    {
        Assert.IsTrue(GriddyIcons.Names.Count > 1000);
        Assert.AreEqual(GriddyIcons.Names.Count, GriddyIcons.Names.Distinct().Count());
        Assert.IsTrue(GriddyIcons.Contains("activity"));
        Assert.IsTrue(GriddyIcons.Contains("ACTIVITY"));
    }

    [TestMethod]
    public void GriddyIcons_AllPaths_ParseToSkPath()
    {
        var failed = GriddyIcons.Names.Where(n => !GriddyIcons.TryGetPath(n, out _)).ToList();
        Assert.AreEqual(0, failed.Count, "Icons failing to parse: " + string.Join(", ", failed.Take(10)));
    }

    [TestMethod]
    public void GriddyIcons_Unknown_ReturnsFalse()
    {
        Assert.IsFalse(GriddyIcons.Contains("definitely_not_an_icon"));
        Assert.IsFalse(GriddyIcons.TryGetPathData("definitely_not_an_icon", out string? pathData));
        Assert.AreEqual("", pathData);
        Assert.IsFalse(GriddyIcons.TryGetPath("", out _));
        Assert.IsFalse(GriddyIcons.TryGetPath(null!, out _));
    }
}
