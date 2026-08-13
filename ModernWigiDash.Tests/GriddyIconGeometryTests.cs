using System.Windows.Media;
using ModernWigiDash.App.Update;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class GriddyIconGeometryTests
{
    [TestMethod]
    public void FromName_UpdateIconNames_Resolve()
    {
        foreach (string name in new[] { "arrow-circle-down", "swap-horizontal", "refresh" })
        {
            Assert.IsNotNull(GriddyIconGeometry.FromName(name), $"'{name}' must resolve from the Griddy map");
        }
    }

    [TestMethod]
    public void FromName_Unknown_ReturnsNull()
        => Assert.IsNull(GriddyIconGeometry.FromName("no-such-icon"));

    [TestMethod]
    public void FromName_IsCaseInsensitive()
    {
        Assert.IsNotNull(GriddyIconGeometry.FromName("Refresh"));
        Assert.IsNotNull(GriddyIconGeometry.FromName("ARROW-CIRCLE-DOWN"));
    }

    [TestMethod]
    public void FromName_SameIcon_ReturnsCachedInstance()
    {
        Assert.AreSame(GriddyIconGeometry.FromName("refresh"), GriddyIconGeometry.FromName("refresh"));
    }

    [TestMethod]
    public void ParsePathData_Empty_ReturnsNull()
        => Assert.IsNull(GriddyIconGeometry.ParsePathData(""));
}
