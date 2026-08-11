using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the widget size floors at their single owner (<see cref="WidgetSizeLimits"/>).
/// The consumers (InspectorValuePolicy's inspector floor, InputController's
/// drag floors) reference these constants directly, so a drift in any floor
/// shows up here as a value change — the two floors can never silently
/// diverge again.
/// </summary>
[TestClass]
public class WidgetSizeLimitsTests
{
    [TestMethod]
    public void InspectorFloor_IsTwenty()
    {
        Assert.AreEqual(20f, WidgetSizeLimits.MinInspectorSize);
    }

    [TestMethod]
    public void DragFloors_AreFortyByThirty()
    {
        Assert.AreEqual(40f, WidgetSizeLimits.MinDragSizeX);
        Assert.AreEqual(30f, WidgetSizeLimits.MinDragSizeY);
    }

    [TestMethod]
    public void InspectorAndDragFloors_AreDeliberatelyDifferentPolicies()
    {
        // The inspector floor and the drag floors are two distinct policies
        // (typed-value validation vs. handle-usability), not one value — the
        // test documents that they differ on purpose and stay in the owner.
        Assert.AreNotEqual(WidgetSizeLimits.MinInspectorSize, WidgetSizeLimits.MinDragSizeX);
        Assert.AreNotEqual(WidgetSizeLimits.MinInspectorSize, WidgetSizeLimits.MinDragSizeY);
    }
}
