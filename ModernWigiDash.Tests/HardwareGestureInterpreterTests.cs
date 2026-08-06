using ModernWigiDash.App.Gestures;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class HardwareGestureInterpreterTests
{
    private const int TwoPages = 2;
    private const int FirstPage = 0;
    private const int LastPage = 1;

    private static HardwareGestureInterpreter NewInterpreter() => new();

    [TestMethod]
    public void FirstDown_RecordsStart_AndRoutesTouchDown()
    {
        var g = NewInterpreter();

        var o = g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
        Assert.AreEqual(TouchEventType.TouchDown, o.WidgetTouchType);
        Assert.AreEqual(500, o.X);
        Assert.AreEqual(300, o.Y);
    }

    [TestMethod]
    public void DownWithMovement_RoutesTouchMove_NotTouchDown()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchDown, 530, 310, TwoPages, FirstPage);

        Assert.AreEqual(TouchEventType.TouchMove, o.WidgetTouchType);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void SubPixelMovement_RoutesTouchDown()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchDown, 500.2f, 300.1f, TwoPages, FirstPage);

        Assert.AreEqual(TouchEventType.TouchDown, o.WidgetTouchType);
    }

    [TestMethod]
    public void SwipeLeft_OnFirstPage_AdvancesToNextPage()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 800, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 100, 310, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.NextPage, o.PageAction);
        Assert.IsFalse(o.RouteToWidgets);
    }

    [TestMethod]
    public void SwipeLeft_OnLastPage_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 800, 300, TwoPages, LastPage);

        var o = g.Feed(TouchEventType.TouchUp, 100, 310, TwoPages, LastPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
        Assert.AreEqual(TouchEventType.TouchUp, o.WidgetTouchType);
    }

    [TestMethod]
    public void SwipeRight_OnFirstPage_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 100, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 800, 310, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void SwipeRight_OnLastPage_GoesBackToPreviousPage()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 100, 300, TwoPages, LastPage);

        var o = g.Feed(TouchEventType.TouchUp, 800, 310, TwoPages, LastPage);

        Assert.AreEqual(GesturePageAction.PrevPage, o.PageAction);
        Assert.IsFalse(o.RouteToWidgets);
    }

    [TestMethod]
    public void VerticalDrift_OverTolerance_SuppressesSwipe()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 800, 200, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 100, 500, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void ShortMove_BelowSwipeThreshold_IsJustATap()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 520, 310, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.AreEqual(TouchEventType.TouchUp, o.WidgetTouchType);
    }

    [TestMethod]
    public void TapLeftEdge_GoesBackToPreviousPage()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 30, 300, TwoPages, LastPage);

        var o = g.Feed(TouchEventType.TouchUp, 30, 300, TwoPages, LastPage);

        Assert.AreEqual(GesturePageAction.PrevPage, o.PageAction);
        Assert.IsFalse(o.RouteToWidgets);
    }

    [TestMethod]
    public void TapRightEdge_AdvancesToNextPage()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 990, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 990, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.NextPage, o.PageAction);
        Assert.IsFalse(o.RouteToWidgets);
    }

    [TestMethod]
    public void TapLeftEdge_OnFirstPage_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 30, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 30, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void TapOutsideEdgeZone_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 500, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void RepeatedUp_AfterRelease_IsSwallowed()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);
        g.Feed(TouchEventType.TouchUp, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 500, 300, TwoPages, FirstPage);

        Assert.IsFalse(o.RouteToWidgets);
        Assert.AreEqual(GesturePageAction.None, o.PageAction);
    }

    [TestMethod]
    public void Reset_ClearsActiveState()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);
        g.Reset();

        var o = g.Feed(TouchEventType.TouchUp, 500, 300, TwoPages, FirstPage);

        // Without Reset the Up would complete a gesture; after Reset it is a
        // release with no prior Down, so it is swallowed.
        Assert.IsFalse(o.RouteToWidgets);
    }
}
