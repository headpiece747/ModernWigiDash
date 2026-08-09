using ModernWigiDash.App.Input;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class InputControllerTests
{
    private sealed class TestWidget : ModernWidgetBase
    {
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    private static PlacedWidgetInstance PlaceWidget(float x, float y, float w, float h, IModernWidget? instance = null)
        => new()
        {
            PluginId = "test",
            DisplayName = "Test",
            X = x,
            Y = y,
            Width = w,
            Height = h,
            ZIndex = 1,
            ActiveInstance = instance
        };

    private static PageLayout TwoPages() => new()
    {
        PageName = "TestPage",
        Widgets = [PlaceWidget(0, 0, 406, 148, new TestWidget())]
    };

    // ── gesture navigation through the seam ─────────────────

    [TestMethod]
    public void Feed_SwipeLeft_NavigatesToNextPage()
    {
        int? navigated = null;
        var controller = new InputController(navigateTo: i => navigated = i);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);

        Assert.AreEqual(1, navigated);
    }

    [TestMethod]
    public void Feed_SwipeRight_NavigatesToPreviousPage()
    {
        int? navigated = null;
        var controller = new InputController(navigateTo: i => navigated = i);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 550, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 1, page);
        controller.Feed(TouchEventType.TouchUp, 800, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 1, page);

        Assert.AreEqual(0, navigated);
    }

    [TestMethod]
    public void Feed_ArrowRightTap_NavigatesToNextPage()
    {
        int? navigated = null;
        var controller = new InputController(navigateTo: i => navigated = i);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 990, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);
        controller.Feed(TouchEventType.TouchUp, 990, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);

        Assert.AreEqual(1, navigated);
    }

    [TestMethod]
    public void Feed_ArrowLeftTap_NavigatesToPreviousPage()
    {
        int? navigated = null;
        var controller = new InputController(navigateTo: i => navigated = i);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 30, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 1, page);
        controller.Feed(TouchEventType.TouchUp, 30, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 1, page);

        Assert.AreEqual(0, navigated);
    }

    [TestMethod]
    public void Feed_SwipeLeftAtLastPage_DoesNotNavigate()
    {
        int navigations = 0;
        var controller = new InputController(navigateTo: _ => navigations++);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 1, page);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 1, page);

        Assert.AreEqual(0, navigations, "Page bounds must suppress navigation at the last page");
    }

    // ── edit-mode veto ──────────────────────────────────────

    [TestMethod]
    public void Feed_TapOutsideEditMode_RoutesToWidgets()
    {
        int routed = 0;
        var controller = new InputController(routeTouch: (_, _, _, _) => routed++);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);

        Assert.AreEqual(2, routed, "A tap routes both Down and Up outside edit mode");
    }

    [TestMethod]
    public void Feed_TapInEditMode_VetoesWidgetRouting()
    {
        int routed = 0;
        var controller = new InputController(routeTouch: (_, _, _, _) => routed++);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: true, pageCount: 2, activePageIndex: 0, page);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: true, pageCount: 2, activePageIndex: 0, page);

        Assert.AreEqual(0, routed, "Edit mode must veto widget routing for every source");
    }

    [TestMethod]
    public void Feed_SwipeInEditMode_StillNavigates()
    {
        int? navigated = null;
        int routed = 0;
        var controller = new InputController(navigateTo: i => navigated = i, routeTouch: (_, _, _, _) => routed++);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: true, pageCount: 2, activePageIndex: 0, page);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: true, pageCount: 2, activePageIndex: 0, page);

        Assert.AreEqual(1, navigated, "Page actions must still apply in edit mode");
        Assert.AreEqual(0, routed);
    }

    // ── manipulation: decision ──────────────────────────────

    [TestMethod]
    public void Begin_OnWidgetInEditMode_StartsDrag()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());

        var kind = controller.Begin(widget, widget, 60, 60, editMode: true);

        Assert.AreEqual(ManipulationKind.Drag, kind);
    }

    [TestMethod]
    public void Begin_OnResizeHandle_StartsResize()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());

        var kind = controller.Begin(widget, widget, 400, 140, editMode: true);

        Assert.AreEqual(ManipulationKind.Resize, kind);
    }

    [TestMethod]
    public void Begin_WhenEditModeOff_ReturnsNone()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());

        var kind = controller.Begin(widget, widget, 60, 60, editMode: false);

        Assert.AreEqual(ManipulationKind.None, kind);
    }

    [TestMethod]
    public void Begin_OnEmptyCanvas_ReturnsNone()
    {
        var controller = new InputController();

        var kind = controller.Begin(null, null, 60, 60, editMode: true);

        Assert.AreEqual(ManipulationKind.None, kind);
    }

    // ── manipulation: drag / resize / snap ──────────────────

    [TestMethod]
    public void Move_Drag_UpdatesWidgetPosition()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        controller.Begin(widget, widget, 60, 60, editMode: true);

        bool consumed = controller.Move(widget, 160, 110, editMode: true, out bool changed);

        Assert.IsTrue(consumed);
        Assert.IsTrue(changed);
        Assert.AreEqual(100f, widget.X, 0.01f);
        Assert.AreEqual(50f, widget.Y, 0.01f);
    }

    [TestMethod]
    public void Move_WithoutManipulation_ReturnsFalse()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());

        bool consumed = controller.Move(widget, 160, 110, editMode: true, out bool changed);

        Assert.IsFalse(consumed);
        Assert.IsFalse(changed);
    }

    [TestMethod]
    public void Move_Resize_UpdatesSizeWithMinimums()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        controller.Begin(widget, widget, 400, 140, editMode: true);

        controller.Move(widget, 20, 20, editMode: true, out _);

        Assert.AreEqual(40f, widget.Width, "Width must clamp to the minimum");
        Assert.AreEqual(30f, widget.Height, "Height must clamp to the minimum");
    }

    [TestMethod]
    public void End_DragWithSnapToGrid_SnapsWidget()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        controller.Begin(widget, widget, 60, 60, editMode: true);
        controller.Move(widget, 250, 160, editMode: true, out _);

        bool wasManipulating = controller.End(widget, editMode: true, snapToGrid: true, out _);

        Assert.IsTrue(wasManipulating);
        Assert.AreEqual(
            (float)Math.Round(190 / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth,
            widget.X, 0.01f);
        Assert.AreEqual(
            (float)Math.Round(100 / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight,
            widget.Y, 0.01f);
    }

    [TestMethod]
    public void End_DragWithoutSnap_KeepsRawPosition()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        controller.Begin(widget, widget, 60, 60, editMode: true);
        controller.Move(widget, 250, 160, editMode: true, out _);

        controller.End(widget, editMode: true, snapToGrid: false, out _);

        Assert.AreEqual(190f, widget.X, 0.01f);
        Assert.AreEqual(100f, widget.Y, 0.01f);
    }

    [TestMethod]
    public void End_WithoutManipulation_ReturnsFalse()
    {
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());

        bool wasManipulating = controller.End(widget, editMode: true, snapToGrid: true, out _);

        Assert.IsFalse(wasManipulating);
    }

    // ── manipulation: icon grab (Hotkey widget) ─────────────

    [TestMethod]
    public void Begin_OnHotkeyIcon_StartsIconGrab()
    {
        string iconName = GriddyIcons.Names.First();
        var hotkey = new HotkeyButtonWidget { Icon = iconName };
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, hotkey);

        var kind = controller.Begin(widget, widget, 203, 46, editMode: true);

        Assert.AreEqual(ManipulationKind.IconGrab, kind);
    }

    [TestMethod]
    public void Begin_OnIconGrabCapabilityWidget_StartsIconGrab_WithoutConcreteType()
    {
        // The controller must drive icon grabs through the IWidgetIconGrab
        // capability — no widget-type branch in the App layer.
        var grab = new FakeIconGrabWidget();
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, grab);

        var kind = controller.Begin(widget, widget, 203, 46, editMode: true);

        Assert.AreEqual(ManipulationKind.IconGrab, kind);
        Assert.AreEqual(1, grab.HitTestCalls);
        controller.End(widget, editMode: true, snapToGrid: true, out _);
    }

    private sealed class FakeIconGrabWidget : ModernWidgetBase, IWidgetIconGrab
    {
        public int HitTestCalls { get; private set; }

        public override void Render(SKCanvas canvas, SKRect bounds) { }

        public bool IsPointOverIcon(float width, float height, float localX, float localY)
        {
            HitTestCalls++;
            return true;
        }

        public bool TryGetIconCenter(float width, float height, out SkiaSharp.SKPoint center, out float half)
        {
            center = new SkiaSharp.SKPoint(width / 2f, height * 0.31f);
            half = 10f;
            return true;
        }

        public bool ApplyGrabMove(PlacedWidgetInstance placed, float localX, float localY, float grabOffsetX, float grabOffsetY)
            => true;
    }

    [TestMethod]
    public void Move_IconGrab_UpdatesIconOffsetsAndPersists()
    {
        string iconName = GriddyIcons.Names.First();
        var hotkey = new HotkeyButtonWidget { Icon = iconName };
        var controller = new InputController();
        var widget = PlaceWidget(0, 0, 406, 148, hotkey);
        // ApplyGrabMove persists through SetProperty → context, so the widget
        // needs a context that resolves the owning placed instance.
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(widget);
        var context = new PersistingContext(profile);
        hotkey.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        controller.Begin(widget, widget, 203, 46, editMode: true);

        bool consumed = controller.Move(widget, 253, 76, editMode: true, out bool changed);

        Assert.IsTrue(consumed);
        Assert.IsTrue(changed, "Icon offset must change when the grab moves");
        Assert.AreNotEqual(0, hotkey.IconOffsetX);
        Assert.AreNotEqual(0, hotkey.IconOffsetY);
        Assert.IsTrue(widget.PropertyValues.ContainsKey("IconOffsetX"), "PropertyValues must persist the offset");
        controller.End(widget, editMode: true, snapToGrid: true, out bool iconMoved);
        Assert.IsTrue(iconMoved);
    }


    [TestMethod]
    public void Feed_Tap_RequestsRenderAfterRouting()
    {
        int renders = 0;
        var controller = new InputController(requestRender: () => renders++);
        var page = TwoPages();

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: false, pageCount: 2, activePageIndex: 0, page);

        Assert.AreEqual(2, renders, "Both routed samples request a canvas refresh");
    }
}
