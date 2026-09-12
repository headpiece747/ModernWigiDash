namespace ModernWigiDash.Tests;

/// <summary>
/// SelectionPolicy at its interface: the same-reference early-out rule that
/// keeps the mutation contract's selection re-application free when nothing
/// changed, pinned without a window or a compositor. The production binding in
/// MainWindow routes every selection change through this module first; the
/// existing ProfileMutationContractTests still cover the end-to-end behavior
/// on a live STA window.
/// </summary>
[TestClass]
public class SelectionPolicyTests
{
    [TestMethod]
    public void ShouldSkip_SameReference_ReturnsTrue()
    {
        var widget = new PlacedWidgetInstance();

        Assert.IsTrue(SelectionPolicy.ShouldSkip(widget, widget));
    }

    [TestMethod]
    public void ShouldSkip_BothNull_ReturnsTrue()
    {
        Assert.IsTrue(SelectionPolicy.ShouldSkip(null, null));
    }

    [TestMethod]
    public void ShouldSkip_DifferentInstances_ReturnsFalse()
    {
        var a = new PlacedWidgetInstance();
        var b = new PlacedWidgetInstance();

        Assert.IsFalse(SelectionPolicy.ShouldSkip(a, b));
    }

    [TestMethod]
    public void ShouldSkip_CurrentNullNextNonNull_ReturnsFalse()
    {
        var widget = new PlacedWidgetInstance();

        Assert.IsFalse(SelectionPolicy.ShouldSkip(null, widget));
    }

    [TestMethod]
    public void ShouldSkip_CurrentNonNullNextNull_ReturnsFalse()
    {
        var widget = new PlacedWidgetInstance();

        Assert.IsFalse(SelectionPolicy.ShouldSkip(widget, null));
    }
}
