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
