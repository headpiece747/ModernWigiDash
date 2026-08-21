using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Tests;

/// <summary>
/// The page-input routing module's tests — extracted from the compositor's
/// test file when routing moved out of the renderer (subject files own their
/// tests).
/// </summary>
[TestClass]
public class WidgetRoutingTests
{
    private sealed class SolidWidget : ModernWidgetBase
    {
        public override void Render(SKCanvas canvas, SKRect bounds)
        {
        }
    }

    private sealed class RecordingWidget : ModernWidgetBase
    {
        public SKPoint? LastLocalPoint { get; private set; }

        public override void Render(SKCanvas canvas, SKRect bounds)
        {
        }

        public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
        {
            LastLocalPoint = localPoint;
        }
    }

    private static PlacedWidgetInstance Widget(float x, float y, float w, float h, IModernWidget instance) => new()
    {
        PluginId = "solid",
        DisplayName = "Solid",
        X = x,
        Y = y,
        Width = w,
        Height = h,
        ZIndex = 1,
        ActiveInstance = instance
    };

    [TestMethod]
    public void HitTest_RotatedWidget_AnswersWhereDrawnNotBoundingBox()
    {
        var widget = Widget(0, 0, 200, 100, new SolidWidget());
        widget.Rotation = 90f;
        var page = new PageLayout { Widgets = [widget] };

        // 90° about the center turns the 200x100 rect into a 100x200 drawn
        // footprint spanning x in [50,150], y in [-50,150]. (100,140) is inside
        // that footprint but below the unrotated box (y>100), so only the
        // rotation-aware test can hit it. (10,50) is in the unrotated box but
        // left of the drawn footprint (x<50), so it must miss.
        Assert.IsNotNull(WidgetRouting.HitTest(page, 100, 140), "A point inside the drawn footprint must hit");
        Assert.IsNull(WidgetRouting.HitTest(page, 10, 50), "A point in the unrotated box but outside the drawn footprint must miss");
    }

    [TestMethod]
    public void HitTest_RotatedWidget_UnrotatedPointStillHits()
    {
        var widget = Widget(0, 0, 200, 100, new SolidWidget());
        widget.Rotation = 90f;
        var page = new PageLayout { Widgets = [widget] };

        Assert.IsNotNull(WidgetRouting.HitTest(page, 100, 50), "The rotation center must always hit");
    }

    [TestMethod]
    public void HitTest_Rotated45Degrees_InteriorHitsBoundaryMisses()
    {
        // A 45° rotation about the center of a 200x100 rect: the drawn
        // diamond's interior must hit, points beyond its far corner must miss
        // (the unrotated bounding box would wrongly accept them).
        var widget = Widget(0, 0, 200, 100, new SolidWidget());
        widget.Rotation = 45f;
        var page = new PageLayout { Widgets = [widget] };

        Assert.IsNotNull(WidgetRouting.HitTest(page, 100, 50), "The rotation center must always hit");
        Assert.IsNotNull(WidgetRouting.HitTest(page, 140, 75), "An interior point along the rotated extent must hit");
        Assert.IsNull(WidgetRouting.HitTest(page, -10, -10), "A point beyond the rotated corner must miss");
    }

    [TestMethod]
    public void RouteTouch_RotatedWidget_DeliversRotatedLocalCoordinates()
    {
        var recorder = new RecordingWidget();
        var widget = Widget(0, 0, 200, 100, recorder);
        widget.Rotation = 90f;
        var page = new PageLayout { Widgets = [widget] };

        // Global (100,140) is widget-local (190,50) under a 90° rotation about
        // the center — well inside the widget, so no boundary float ambiguity.
        WidgetRouting.RouteTouch(page, 100, 140, TouchEventType.TouchDown);

        Assert.IsNotNull(recorder.LastLocalPoint, "The touch must reach the rotated widget");
        Assert.AreEqual(190f, recorder.LastLocalPoint.Value.X, 0.01f, "Rotated local X must be inverse-transformed");
        Assert.AreEqual(50f, recorder.LastLocalPoint.Value.Y, 0.01f, "Rotated local Y must be inverse-transformed");
    }

    [TestMethod]
    public void HitTest_ReturnsTopMostWidget()
    {
        var page = new PageLayout();
        var w1 = new PlacedWidgetInstance { X = 0, Y = 0, Width = 200, Height = 200, ZIndex = 1, ActiveInstance = new DigitalAnalogClockWidget() };
        var w2 = new PlacedWidgetInstance { X = 50, Y = 50, Width = 200, Height = 200, ZIndex = 2, ActiveInstance = new DigitalAnalogClockWidget() };
        page.Widgets.Add(w1);
        page.Widgets.Add(w2);

        var hit = WidgetRouting.HitTest(page, 75, 75);
        Assert.IsNotNull(hit);
        Assert.AreEqual(w2, hit, "HitTest must return highest ZIndex widget at overlapping point");
    }

    [TestMethod]
    public void HitTest_ZIndexTie_LastWidgetInListWins()
    {
        // The compositor paints equal-ZIndex widgets in list order (stable
        // ascending sort — the later one is on top); the hit-test must agree.
        var page = new PageLayout();
        var w1 = new PlacedWidgetInstance { X = 0, Y = 0, Width = 200, Height = 200, ZIndex = 1, ActiveInstance = new DigitalAnalogClockWidget() };
        var w2 = new PlacedWidgetInstance { X = 50, Y = 50, Width = 200, Height = 200, ZIndex = 1, ActiveInstance = new DigitalAnalogClockWidget() };
        page.Widgets.Add(w1);
        page.Widgets.Add(w2);

        var hit = WidgetRouting.HitTest(page, 75, 75);
        Assert.AreEqual(w2, hit, "A ZIndex tie must resolve to the widget painted on top (the later in list order)");
    }

    [TestMethod]
    public void RouteTouch_DeliversInWidgetLocalCoordinates()
    {
        var recorder = new RecordingWidget();
        var page = new PageLayout();
        page.Widgets.Add(new PlacedWidgetInstance
        {
            X = 100,
            Y = 50,
            Width = 200,
            Height = 200,
            ZIndex = 1,
            ActiveInstance = recorder
        });

        // Touch at (150, 80) global = (50, 30) local to the widget.
        WidgetRouting.RouteTouch(page, 150, 80, TouchEventType.TouchUp);

        Assert.IsNotNull(recorder.LastLocalPoint, "The touch must reach the widget");
        Assert.AreEqual(50f, recorder.LastLocalPoint.Value.X, 0.01f, "Global X must convert to widget-local X");
        Assert.AreEqual(30f, recorder.LastLocalPoint.Value.Y, 0.01f, "Global Y must convert to widget-local Y");
    }

    [TestMethod]
    public void RouteTouch_OverlappingWidgets_DeliversToTopMostOnly()
    {
        var lower = new RecordingWidget();
        var upper = new RecordingWidget();
        var page = new PageLayout();
        page.Widgets.Add(new PlacedWidgetInstance { X = 0, Y = 0, Width = 200, Height = 200, ZIndex = 1, ActiveInstance = lower });
        page.Widgets.Add(new PlacedWidgetInstance { X = 50, Y = 50, Width = 200, Height = 200, ZIndex = 2, ActiveInstance = upper });

        WidgetRouting.RouteTouch(page, 75, 75, TouchEventType.TouchDown);

        Assert.IsNotNull(upper.LastLocalPoint, "The top-most widget must receive the touch");
        Assert.IsNull(lower.LastLocalPoint, "A touch consumed by the top-most widget must never propagate to the one underneath");
    }

    [TestMethod]
    public void RouteTouch_IgnoresPointOutsideAllWidgets()
    {
        var recorder = new RecordingWidget();
        var page = new PageLayout();
        page.Widgets.Add(new PlacedWidgetInstance
        {
            X = 0,
            Y = 0,
            Width = 100,
            Height = 100,
            ZIndex = 1,
            ActiveInstance = recorder
        });

        // Point far outside every widget must not throw and must not be delivered.
        WidgetRouting.RouteTouch(page, 900, 500, TouchEventType.TouchUp);

        Assert.IsNull(recorder.LastLocalPoint, "A point outside every widget must not reach any widget");
    }

    [TestMethod]
    public void HitTest_SkipsWidgetsWithoutAnInstance()
    {
        // A placed widget with a null ActiveInstance is not rendered (the
        // compositor skips it) — it must not intercept touches meant for the
        // widget beneath it either.
        var recorder = new RecordingWidget();
        var page = new PageLayout();
        page.Widgets.Add(new PlacedWidgetInstance { X = 0, Y = 0, Width = 200, Height = 200, ZIndex = 2, ActiveInstance = null });
        page.Widgets.Add(new PlacedWidgetInstance { X = 0, Y = 0, Width = 200, Height = 200, ZIndex = 1, ActiveInstance = recorder });

        var hit = WidgetRouting.HitTest(page, 50, 50);
        Assert.AreSame(recorder, hit?.ActiveInstance, "the no-instance widget must not be hit-testable");
    }

    [TestMethod]
    public void RouteTouch_EmptyPage_DoesNotThrow()
    {
        var page = new PageLayout();

        WidgetRouting.RouteTouch(page, 10, 10, TouchEventType.TouchDown);

        Assert.AreEqual(0, page.Widgets.Count, "Routing on an empty page must not mutate the page");
    }
}
