using ModernWigiDash.Core.Rendering;

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
    public void Compose_OpaqueAndTranslucentWidgets_BothPaint()
    {
        // A widget with Opacity < 0.99 takes the save-layer alpha path (the
        // _alphaPaint + SaveLayer/Restore legs), distinct from the opaque fast
        // path. The translucent interior must still be present (a blend toward
        // the background, not a hole).
        using var compositor = new SkiaFrameCompositor();
        var widget = Widget(100, 100, 200, 150, new SolidWidget(new SKColor(200, 30, 40)));
        widget.Opacity = 0.5f;
        var page = new PageLayout { Widgets = [widget] };

        compositor.Compose(page);

        var interior = PixelAt(compositor, 150, 150);
        Assert.AreNotEqual(PageBackground, interior, "A translucent widget must still paint its bounds");
        Assert.IsTrue(interior.Alpha > 0 && interior.Red > PageBackground.Red,
            "The translucent interior must blend the widget color over the background");
    }

    [TestMethod]
    public void Compose_MoreThanThirtyTwoWidgets_TakesTheLinQSortPath()
    {
        // The small-page fast path (stack-allocated insertion sort) handles <= 32
        // widgets; a page with more routes through the LINQ OrderBy fallback.
        // Pin that the larger path composes every widget without throwing and
        // paints the last one on top.
        using var compositor = new SkiaFrameCompositor();
        var widgets = new List<PlacedWidgetInstance>();
        for (int i = 0; i < 34; i++)
        {
            byte r = (byte)(i * 7 % 256);
            var w = Widget(0, 0, 100, 100, new SolidWidget(new SKColor(r, 0, 0)));
            w.ZIndex = i;
            widgets.Add(w);
        }
        var page = new PageLayout { Widgets = widgets };

        compositor.Compose(page); // must not throw on the oversized page

        // The highest-ZIndex widget (i=33) painted last and owns the center pixel.
        byte topR = (byte)(33 * 7 % 256);
        Assert.AreEqual(new SKColor(topR, 0, 0, 255), PixelAt(compositor, 50, 50));
    }

    [TestMethod]
    public void Compose_PlacementWithoutAnInstance_IsSkipped()
    {
        // A PlacedWidgetInstance whose ActiveInstance is null (a placement that
        // failed to hydrate) must be skipped, not drawn or thrown on.
        using var compositor = new SkiaFrameCompositor();
        var ghost = new PlacedWidgetInstance
        {
            PluginId = "solid",
            DisplayName = "Solid",
            X = 100,
            Y = 100,
            Width = 200,
            Height = 150,
            ZIndex = 1,
            ActiveInstance = null
        };
        var real = Widget(100, 100, 200, 150, new SolidWidget(new SKColor(200, 30, 40)));
        var page = new PageLayout { Widgets = [ghost, real] };

        compositor.Compose(page); // must not throw on the null-instance placement

        Assert.AreEqual(new SKColor(200, 30, 40, 255), PixelAt(compositor, 150, 150),
            "The hydrated widget must still paint alongside the skipped ghost");
    }

    [TestMethod]
    public void Compose_OutOfOrderZIndex_SortsViaInsertionShift()
    {
        // A descending ZIndex order forces the insertion sort's shift leg (the
        // existing order test uses an already-sorted pair, so the swap never
        // runs). The higher-ZIndex widget must still end up on top.
        using var compositor = new SkiaFrameCompositor();
        var upper = Widget(0, 0, 400, 300, new SolidWidget(new SKColor(0, 0, 255)));
        upper.ZIndex = 3;
        var middle = Widget(0, 0, 400, 300, new SolidWidget(new SKColor(0, 255, 0)));
        middle.ZIndex = 2;
        var lower = Widget(0, 0, 400, 300, new SolidWidget(new SKColor(255, 0, 0)));
        lower.ZIndex = 1;
        var page = new PageLayout { Widgets = [upper, middle, lower] };

        compositor.Compose(page);

        Assert.AreEqual(new SKColor(0, 0, 255, 255), PixelAt(compositor, 200, 150),
            "The highest-ZIndex widget must paint last even when listed first");
    }

    [TestMethod]
    public void IsEditMode_And_SelectedWidget_Getters_ReturnTheSetValues()
    {
        // The edit-state property getters (the App reads them to sync the canvas
        // chrome) must return exactly what was set.
        using var compositor = new SkiaFrameCompositor();
        var widget = Widget(0, 0, 100, 100, new SolidWidget(SKColors.Red));

        compositor.IsEditMode = true;
        compositor.SelectedWidget = widget;

        Assert.IsTrue(compositor.IsEditMode, "the IsEditMode getter must reflect the set value");
        Assert.AreSame(widget, compositor.SelectedWidget, "the SelectedWidget getter must return the set instance");
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

}
