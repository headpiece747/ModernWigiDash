using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class PresentMonPresentModeTests
{
    [TestMethod]
    public void FullName_EveryPresentMonId_MapsToCanonicalName()
    {
        Assert.AreEqual("Unknown", PresentMonPresentMode.FullName(0));
        Assert.AreEqual("Hardware Legacy Flip", PresentMonPresentMode.FullName(1));
        Assert.AreEqual("Hardware Legacy Copy to Front Buffer", PresentMonPresentMode.FullName(2));
        Assert.AreEqual("Hardware Independent Flip", PresentMonPresentMode.FullName(3));
        Assert.AreEqual("Composed Flip", PresentMonPresentMode.FullName(4));
        Assert.AreEqual("Composed Copy with GPU GDI", PresentMonPresentMode.FullName(5));
        Assert.AreEqual("Composed Copy with CPU GDI", PresentMonPresentMode.FullName(6));
        Assert.AreEqual("Hardware Composed: Independent Flip", PresentMonPresentMode.FullName(8));
    }

    [TestMethod]
    public void FullName_UnknownId_ReturnsDash()
    {
        Assert.AreEqual("—", PresentMonPresentMode.FullName(-1));
        Assert.AreEqual("—", PresentMonPresentMode.FullName(7));
        Assert.AreEqual("—", PresentMonPresentMode.FullName(999));
    }

    [TestMethod]
    public void ShortName_EveryPresentMonId_MapsToCompactLabel()
    {
        Assert.AreEqual("Unknown", PresentMonPresentMode.ShortName(0));
        Assert.AreEqual("HW Legacy Flip", PresentMonPresentMode.ShortName(1));
        Assert.AreEqual("HW Copy to Front", PresentMonPresentMode.ShortName(2));
        Assert.AreEqual("HW Ind. Flip", PresentMonPresentMode.ShortName(3));
        Assert.AreEqual("Composed Flip", PresentMonPresentMode.ShortName(4));
        Assert.AreEqual("Comp. Copy (GPU)", PresentMonPresentMode.ShortName(5));
        Assert.AreEqual("Comp. Copy (CPU)", PresentMonPresentMode.ShortName(6));
        Assert.AreEqual("HWC Ind. Flip", PresentMonPresentMode.ShortName(8));
    }

    [TestMethod]
    public void ShortName_UnknownId_ReturnsDash()
    {
        Assert.AreEqual("—", PresentMonPresentMode.ShortName(-1));
        Assert.AreEqual("—", PresentMonPresentMode.ShortName(999));
    }
}
