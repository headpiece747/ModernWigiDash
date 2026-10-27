using ModernWigiDash.App.Gestures;

namespace ModernWigiDash.Tests;

[TestClass]
public class GestureInterpreterTests
{
    private const int TwoPages = 2;
    private const int FirstPage = 0;
    private const int LastPage = 1;

    private static GestureInterpreter NewInterpreter() => new();

    [TestMethod]
    public void FirstDown_RecordsStart_AndRoutesTouchDown()
    {
        var g = NewInterpreter();

        var o = g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
        Assert.AreEqual(TouchEventType.TouchDown, o.WidgetTouchType);
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
    public void TapLeftEdge_DoesNotNavigate_RoutesToWidgets()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 30, 300, TwoPages, LastPage);

        var o = g.Feed(TouchEventType.TouchUp, 30, 300, TwoPages, LastPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void TapRightEdge_DoesNotNavigate_RoutesToWidgets()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 990, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 990, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
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

    // ------------------------------------------------------------------
    // Mouse-style contract: the desktop mouse feeds the same machine using
    // Down -> TouchMove -> TouchUp (one Down, explicit Move events), unlike
    // the hardware path which reports Down for both contact and movement.
    // ------------------------------------------------------------------

    [TestMethod]
    public void MouseStyle_DownMoveMoveUp_SwipeLeft_AdvancesToNextPage()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 800, 300, TwoPages, FirstPage);
        g.Feed(TouchEventType.TouchMove, 500, 305, TwoPages, FirstPage);
        g.Feed(TouchEventType.TouchMove, 200, 310, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 100, 310, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.NextPage, o.PageAction);
        Assert.IsFalse(o.RouteToWidgets);
    }

    [TestMethod]
    public void MouseStyle_Moves_WithoutSwipe_RouteAsTouchMove()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchMove, 505, 305, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
        Assert.AreEqual(TouchEventType.TouchMove, o.WidgetTouchType);
    }

    [TestMethod]
    public void MouseStyle_DownMoveUp_SmallDelta_IsATap()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);
        g.Feed(TouchEventType.TouchMove, 505, 305, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 508, 308, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
        Assert.AreEqual(TouchEventType.TouchUp, o.WidgetTouchType);
    }

    [TestMethod]
    public void TouchMove_WithoutActiveGesture_RoutesMove()
    {
        var g = NewInterpreter();

        var o = g.Feed(TouchEventType.TouchMove, 500, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
        Assert.AreEqual(TouchEventType.TouchMove, o.WidgetTouchType);
    }

    // ------------------------------------------------------------------
    // Canonical boundary pins: the mouse now inherits these constants, so
    // the thresholds the unification depends on are locked to the spec.
    // ------------------------------------------------------------------

    [TestMethod]
    public void SwipeDelta_ExactlyThreshold_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 430, 310, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void SwipeDelta_JustOverThreshold_Navigates()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 500, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 429, 310, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.NextPage, o.PageAction);
    }

    [TestMethod]
    public void StationaryTap_AtLeftEdgeBoundary_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 60, 200, TwoPages, LastPage);

        var o = g.Feed(TouchEventType.TouchUp, 60, 200, TwoPages, LastPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }

    [TestMethod]
    public void StationaryTap_AtRightEdgeBoundary_DoesNotNavigate()
    {
        var g = NewInterpreter();
        g.Feed(TouchEventType.TouchDown, 964, 300, TwoPages, FirstPage);

        var o = g.Feed(TouchEventType.TouchUp, 964, 300, TwoPages, FirstPage);

        Assert.AreEqual(GesturePageAction.None, o.PageAction);
        Assert.IsTrue(o.RouteToWidgets);
    }
}
