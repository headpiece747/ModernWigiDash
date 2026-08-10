using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class SkiaFrameCompositorTests
{
    private static readonly SKColor PageBackground = new(18, 20, 29, 255); // #12141D — PageLayout default

    private sealed class SolidWidget : ModernWidgetBase
    {
        private readonly SKColor _color;
        public SolidWidget(SKColor color) => _color = color;

        public override void Render(SKCanvas canvas, SKRect bounds)
        {
            using var paint = new SKPaint { Color = _color, IsAntialias = false };
            canvas.DrawRect(bounds, paint);
        }
    }

    private sealed class RecordingWidget : ModernWidgetBase
    {
        public SKPoint? LastLocalPoint { get; private set; }
        public TouchEventType? LastEventType { get; private set; }

        public override void Render(SKCanvas canvas, SKRect bounds)
        {
        }

        public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
        {
            LastLocalPoint = localPoint;
            LastEventType = eventType;
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

    private static SKColor PixelAt(SkiaFrameCompositor compositor, int x, int y)
    {
        using var pixmap = compositor.FrameBuffer.PeekPixels();
        var bytes = new byte[pixmap.Info.BytesPerPixel];
        nint ptr = pixmap.GetPixels() + (nint)((long)y * pixmap.RowBytes + x * pixmap.Info.BytesPerPixel);
        System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, bytes.Length);
        return new SKColor(bytes[2], bytes[1], bytes[0], 255);
    }

    [TestMethod]
    public void Compose_WidgetPaintsItsBounds()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout
        {
            Widgets = [Widget(100, 100, 200, 150, new SolidWidget(new SKColor(200, 30, 40)))]
        };

        compositor.Compose(page);

        Assert.AreEqual(new SKColor(200, 30, 40, 255), PixelAt(compositor, 150, 150), "Widget interior must be painted");
        Assert.AreEqual(PageBackground, PixelAt(compositor, 10, 10), "Outside the widget the page background must show");
    }

    [TestMethod]
    public void Compose_WidgetsRenderInZIndexOrder()
    {
        using var compositor = new SkiaFrameCompositor();
        var lower = Widget(0, 0, 400, 300, new SolidWidget(new SKColor(255, 0, 0)));
        lower.ZIndex = 1;
        var upper = Widget(0, 0, 400, 300, new SolidWidget(new SKColor(0, 255, 0)));
        upper.ZIndex = 2;
        var page = new PageLayout { Widgets = [lower, upper] };

        compositor.Compose(page);

        Assert.AreEqual(new SKColor(0, 255, 0, 255), PixelAt(compositor, 200, 150), "The higher ZIndex widget must paint last (on top)");
    }

    [TestMethod]
    public void Compose_SelectedWidgetInEditMode_DrawsSelectionBorder()
    {
        using var compositor = new SkiaFrameCompositor { IsEditMode = true };
        var widget = Widget(100, 100, 200, 150, new SolidWidget(new SKColor(40, 40, 40)));
        compositor.SelectedWidget = widget;
        var page = new PageLayout { Widgets = [widget] };

        // Sample the border line before and after selection — the stroke is
        // anti-aliased, so assert the behavioral change, not an exact blend.
        compositor.Compose(page);
        var selectedPixel = PixelAt(compositor, 150, 100);

        compositor.SelectedWidget = null;
        compositor.Compose(page);
        var unselectedPixel = PixelAt(compositor, 150, 100);

        Assert.AreNotEqual(unselectedPixel, selectedPixel, "Edit mode must draw the selection border on the selected widget");
    }

    [TestMethod]
    public void Compose_NoSelectionInEditMode_NoSelectionBorder()
    {
        using var compositor = new SkiaFrameCompositor { IsEditMode = true };
        var widget = Widget(100, 100, 200, 150, new SolidWidget(new SKColor(40, 40, 40)));
        var page = new PageLayout { Widgets = [widget] };

        compositor.Compose(page);

        Assert.AreEqual(new SKColor(40, 40, 40, 255), PixelAt(compositor, 150, 101), "Without a selection no border may overlay the widget");
    }

    [TestMethod]
    public void Compose_EmptyPage_BackgroundOnly()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();

        compositor.Compose(page);

        Assert.AreEqual(PageBackground, PixelAt(compositor, 30, 8));
        Assert.AreEqual(PageBackground, PixelAt(compositor, 500, 300));
    }

    [TestMethod]
    public void Compose_TranslatedAndRotatedWidget_PaintsWithinBounds()
    {
        using var compositor = new SkiaFrameCompositor();
        var widget = Widget(300, 200, 200, 150, new SolidWidget(new SKColor(10, 200, 90)));
        widget.Rotation = 45f;
        var page = new PageLayout { Widgets = [widget] };

        compositor.Compose(page);

        // The rotation keeps the widget near its anchor; far corners stay background.
        Assert.AreEqual(new SKColor(10, 200, 90, 255), PixelAt(compositor, 350, 250), "Rotated widget interior must still paint");
        Assert.AreEqual(PageBackground, PixelAt(compositor, 10, 550));
    }

    [TestMethod]
    public void HitTest_RotatedWidget_AnswersWhereDrawnNotBoundingBox()
    {
        var widget = Widget(0, 0, 200, 100, new SolidWidget(SKColors.Green));
        widget.Rotation = 90f;
        var page = new PageLayout { Widgets = [widget] };

        // 90° about the center turns the 200x100 rect into a 100x200 drawn
        // footprint spanning x in [50,150], y in [-50,150]. (100,140) is inside
        // that footprint but below the unrotated box (y>100), so only the
        // rotation-aware test can hit it. (10,50) is in the unrotated box but
        // left of the drawn footprint (x<50), so it must miss.
        Assert.IsNotNull(SkiaFrameCompositor.HitTest(page, 100, 140), "A point inside the drawn footprint must hit");
        Assert.IsNull(SkiaFrameCompositor.HitTest(page, 10, 50), "A point in the unrotated box but outside the drawn footprint must miss");
    }

    [TestMethod]
    public void HitTest_RotatedWidget_UnrotatedPointStillHits()
    {
        var widget = Widget(0, 0, 200, 100, new SolidWidget(SKColors.Green));
        widget.Rotation = 90f;
        var page = new PageLayout { Widgets = [widget] };

        Assert.IsNotNull(SkiaFrameCompositor.HitTest(page, 100, 50), "The rotation center must always hit");
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
        SkiaFrameCompositor.RouteTouch(page, 100, 140, TouchEventType.TouchDown);

        Assert.IsNotNull(recorder.LastLocalPoint, "The touch must reach the rotated widget");
        Assert.AreEqual(190f, recorder.LastLocalPoint.Value.X, 0.01f, "Rotated local X must be inverse-transformed");
        Assert.AreEqual(50f, recorder.LastLocalPoint.Value.Y, 0.01f, "Rotated local Y must be inverse-transformed");
    }

    [TestMethod]
    public void SkiaFrameCompositor_HitTest_ReturnsTopMostWidget()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();

        var w1 = new PlacedWidgetInstance
        {
            X = 0,
            Y = 0,
            Width = 200,
            Height = 200,
            ZIndex = 1,
            ActiveInstance = new DigitalAnalogClockWidget()
        };
        var w2 = new PlacedWidgetInstance
        {
            X = 50,
            Y = 50,
            Width = 200,
            Height = 200,
            ZIndex = 2,
            ActiveInstance = new DigitalAnalogClockWidget()
        };

        page.Widgets.Add(w1);
        page.Widgets.Add(w2);

        var hit = SkiaFrameCompositor.HitTest(page, 75, 75);
        Assert.IsNotNull(hit);
        Assert.AreEqual(w2, hit, "HitTest must return highest ZIndex widget at overlapping point");
    }

    [TestMethod]
    public void SkiaFrameCompositor_RouteTouch_DeliversToTopMostWidgetInLocalCoordinates()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();
        var target = new WeatherForecastWidget();
        var placed = new PlacedWidgetInstance
        {
            X = 100,
            Y = 50,
            Width = 200,
            Height = 200,
            ZIndex = 1,
            ActiveInstance = target
        };
        page.Widgets.Add(placed);

        // Touch at (150, 80) global = (50, 30) local to the widget. The weather
        // widget's top-left corner cycles LayoutMode on TouchUp, which proves
        // the touch arrived in widget-local coordinates (a global-coordinate
        // leak would hit a different zone or miss entirely).
        SkiaFrameCompositor.RouteTouch(page, 150, 80, TouchEventType.TouchUp);

        Assert.AreEqual("Daily Forecast", target.LayoutMode, "The touch must reach the widget in local coordinates");
    }

    [TestMethod]
    public void SkiaFrameCompositor_RouteTouch_IgnoresPointOutsideAllWidgets()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();
        var target = new WeatherForecastWidget();
        page.Widgets.Add(new PlacedWidgetInstance
        {
            X = 0,
            Y = 0,
            Width = 100,
            Height = 100,
            ZIndex = 1,
            ActiveInstance = target
        });

        // Point far outside every widget must not throw and must not be delivered.
        SkiaFrameCompositor.RouteTouch(page, 900, 500, TouchEventType.TouchUp);

        Assert.AreEqual("Detailed", target.LayoutMode, "A point outside every widget must not reach any widget");
    }

    [TestMethod]
    public void SkiaFrameCompositor_RouteTouch_EmptyPage_DoesNotThrow()
    {
        var page = new PageLayout();

        SkiaFrameCompositor.RouteTouch(page, 10, 10, TouchEventType.TouchDown);

        Assert.AreEqual(0, page.Widgets.Count, "Routing on an empty page must not mutate the page");
    }
}
