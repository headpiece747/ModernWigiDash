using ModernWigiDash.App;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class IconValuePolicyTests
{
    [TestMethod]
    public void IsNamed_EveryCatalogName_IsTrue()
    {
        foreach (string name in GriddyIcons.Names)
        {
            Assert.IsTrue(IconValuePolicy.IsNamed(name), $"{name} must read as named");
        }
    }

    [TestMethod]
    public void IsNamed_CustomPathOrBlank_IsFalse()
    {
        Assert.IsFalse(IconValuePolicy.IsNamed("icons/custom.svg"));
        Assert.IsFalse(IconValuePolicy.IsNamed(""));
        Assert.IsFalse(IconValuePolicy.IsNamed("   "));
        Assert.IsFalse(IconValuePolicy.IsNamed(null));
    }

    [TestMethod]
    public void IsCustom_CustomPath_IsTrue()
    {
        Assert.IsTrue(IconValuePolicy.IsCustom("icons/custom.svg"));
    }

    [TestMethod]
    public void IsCustom_CatalogNameOrBlank_IsFalse()
    {
        string named = GriddyIcons.Names.First();
        Assert.IsFalse(IconValuePolicy.IsCustom(named));
        Assert.IsFalse(IconValuePolicy.IsCustom(""));
        Assert.IsFalse(IconValuePolicy.IsCustom(null));
    }

    [TestMethod]
    public void ResolveCurrent_FileWinsOverNamed()
    {
        string named = GriddyIcons.Names.First();
        Assert.AreEqual("icons/a.svg", IconValuePolicy.ResolveCurrent(named, "icons/a.svg"),
            "The icon file path wins over the named icon");
    }

    [TestMethod]
    public void ResolveCurrent_BlankFile_FallsBackToNamed()
    {
        string named = GriddyIcons.Names.First();
        Assert.AreEqual(named, IconValuePolicy.ResolveCurrent(named, ""));
        Assert.AreEqual(named, IconValuePolicy.ResolveCurrent(named, null));
    }

    [TestMethod]
    public void ResolveCurrent_NeitherValue_IsEmptyString()
    {
        Assert.AreEqual("", IconValuePolicy.ResolveCurrent(null, null));
        Assert.AreEqual("", IconValuePolicy.ResolveCurrent("", ""));
    }

    [TestMethod]
    public void SplitWriteback_NamedSelection_ClearsTheIconFile()
    {
        string named = GriddyIcons.Names.First();
        (string n, string f) = IconValuePolicy.SplitWriteback(named);
        Assert.AreEqual(named, n);
        Assert.AreEqual("", f);
    }

    [TestMethod]
    public void SplitWriteback_CustomSelection_ClearsTheNamedIcon()
    {
        (string n, string f) = IconValuePolicy.SplitWriteback("icons/b.svg");
        Assert.AreEqual("", n);
        Assert.AreEqual("icons/b.svg", f);
    }

    [TestMethod]
    public void SplitWriteback_ExactlyOneCompanionHoldsTheValue()
    {
        foreach (string candidate in new[] { GriddyIcons.Names.First(), "icons/c.svg" })
        {
            (string n, string f) = IconValuePolicy.SplitWriteback(candidate);
            int holders = (n.Length > 0 ? 1 : 0) + (f.Length > 0 ? 1 : 0);
            Assert.AreEqual(1, holders, "Exactly one of the companion properties may hold the chosen value");
        }
    }
}
