using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Tests;

[TestClass]
public class EditOverlayTests
{
    private static readonly SKColor Background = new(18, 20, 29);

    private static SKSurface CreateSurface()
    {
        var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(Background);
        return surface;
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        return bitmap.GetPixel(x, y);
    }

    private static PageLayout Page(bool snapToGrid = true) => new() { SnapToGrid = snapToGrid };

    private static PlacedWidgetInstance Widget() => new()
    {
        DisplayName = "Test Widget",
        X = 0,
        Y = 0,
        Width = 200,
        Height = 150,
        ZIndex = 3
    };

    [TestMethod]
    public void DrawGrid_EditModeOnSnapToGrid_DrawsGridLines()
    {
        using var surface = CreateSurface();
        var overlay = new EditOverlay();

        overlay.DrawGrid(surface.Canvas, Page(snapToGrid: true), editMode: true);

        Assert.AreNotEqual(PixelAt(surface, 101, 150), PixelAt(surface, 203, 150),
            "A vertical grid line must be drawn at x=203 in edit mode with snap-to-grid");
    }

    [TestMethod]
    public void DrawGrid_EditModeOff_LeavesCanvasUntouched()
    {
        using var surface = CreateSurface();
        var overlay = new EditOverlay();

        overlay.DrawGrid(surface.Canvas, Page(), editMode: false);

        Assert.AreEqual(Background, PixelAt(surface, 203, 150));
        Assert.AreEqual(Background, PixelAt(surface, 100, 148));
    }

    [TestMethod]
    public void DrawGrid_SnapToGridDisabled_LeavesCanvasUntouched()
    {
        using var surface = CreateSurface();
        var overlay = new EditOverlay();

        overlay.DrawGrid(surface.Canvas, Page(snapToGrid: false), editMode: true);

        Assert.AreEqual(Background, PixelAt(surface, 203, 150));
    }

    [TestMethod]
    public void DrawSelection_SelectedInEditMode_DrawsChrome()
    {
        using var surface = CreateSurface();
        var overlay = new EditOverlay();

        overlay.DrawSelection(surface.Canvas, Widget(), editMode: true, isSelected: true);

        // Resize handle: local (184,134)..(198,148) for a 200x150 widget — blue fill over background.
        Assert.AreNotEqual(Background, PixelAt(surface, 191, 141), "The resize handle must be drawn");
        // Selection border: top edge stroke of the widget bounds.
        Assert.AreNotEqual(Background, PixelAt(surface, 100, 0), "The selection border must be drawn");
    }

    [TestMethod]
    public void DrawSelection_NotSelected_LeavesCanvasUntouched()
    {
        using var surface = CreateSurface();
        var overlay = new EditOverlay();

        overlay.DrawSelection(surface.Canvas, Widget(), editMode: true, isSelected: false);

        Assert.AreEqual(Background, PixelAt(surface, 191, 141));
        Assert.AreEqual(Background, PixelAt(surface, 100, 0));
    }

    [TestMethod]
    public void DrawSelection_EditModeOff_LeavesCanvasUntouched()
    {
        using var surface = CreateSurface();
        var overlay = new EditOverlay();

        overlay.DrawSelection(surface.Canvas, Widget(), editMode: false, isSelected: true);

        Assert.AreEqual(Background, PixelAt(surface, 191, 141));
        Assert.AreEqual(Background, PixelAt(surface, 100, 0));
    }
}
