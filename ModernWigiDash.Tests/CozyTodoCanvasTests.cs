namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets.Todo;

[TestClass]
public class CozyTodoCanvasTests
{
    [TestMethod]
    public void HandleTap_UnprimedCanvas_ResolvesToNoHit()
    {
        // The precondition pin: a canvas that has never rendered has no
        // drawn rows, so every tap resolves to None (the default geometry's
        // empty rects), never a fault.
        using var canvas = new CozyTodoCanvas();

        var action = canvas.HandleTapForTest(new SKPoint(500f, 300f));

        Assert.AreEqual(CozyTodoHitKind.None, action.Kind);
    }
}
