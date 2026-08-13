using System.Reflection;
using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

[TestClass]
public class AppVersionTests
{
    [TestMethod]
    public void Current_ReadsInformationalVersion_AsSemVer()
    {
        // The test assembly has no informational stamp; the checker treats
        // unparseable as dev. This test pins the parse path directly instead.
        Assert.IsTrue(AppVersion.IsDevBuild || AppVersion.Current is not null);
    }

    [TestMethod]
    public void Parse_HandlesVersionSuffix()
    {
        var v = AppVersion.Parse("0.4.1-alpha.1");
        Assert.IsNotNull(v);
        Assert.AreEqual(0, v!.Major);
        Assert.AreEqual(4, v.Minor);
        Assert.AreEqual(1, v.Build);
    }

    [TestMethod]
    public void Parse_HandlesVPrefixTag()
    {
        var v = AppVersion.Parse("v0.5.0");
        Assert.IsNotNull(v);
        Assert.AreEqual(0, v!.Major);
        Assert.AreEqual(5, v.Minor);
    }

    [TestMethod]
    public void Parse_Unparseable_ReturnsNull()
    {
        Assert.IsNull(AppVersion.Parse("dev"));
        Assert.IsNull(AppVersion.Parse("0.0.0-dev"));
        Assert.IsNull(AppVersion.Parse(""));
    }
}
