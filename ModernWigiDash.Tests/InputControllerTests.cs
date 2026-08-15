using ModernWigiDash.App.Input;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class InputControllerTests
{
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

    private static InputState StateOf(PageLayout page, int index, int count = 2)
        => new(page, count, index);

    private static PageLayout PageWith(PlacedWidgetInstance widget, bool snapToGrid = false) => new()
    {
        PageName = "TestPage",
        Widgets = [widget],
        SnapToGrid = snapToGrid
    };

    // ── gesture navigation through the seam ─────────────────

    [TestMethod]
    public void Feed_SwipeLeft_NavigatesToNextPage()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: false);

        Assert.AreEqual(1, navigated);
    }

    [TestMethod]
    public void Feed_SwipeRight_NavigatesToPreviousPage()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 1), navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 550, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 800, 300, suppressWidgetRouting: false);

        Assert.AreEqual(0, navigated);
    }

    [TestMethod]
    public void Feed_ArrowRightTap_NavigatesToNextPage()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 990, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 990, 300, suppressWidgetRouting: false);

        Assert.AreEqual(1, navigated);
    }

    [TestMethod]
    public void Feed_ArrowLeftTap_NavigatesToPreviousPage()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 1), navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 30, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 30, 300, suppressWidgetRouting: false);

        Assert.AreEqual(0, navigated);
    }

    [TestMethod]
    public void Feed_SwipeLeftAtLastPage_DoesNotNavigate()
    {
        int navigations = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 1), navigateTo: _ => navigations++);

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: false);

        Assert.AreEqual(0, navigations, "Page bounds must suppress navigation at the last page");
    }

    // ── edit-mode veto ──────────────────────────────────────

    [TestMethod]
    public void Feed_TapOutsideEditMode_RoutesToWidgets()
    {
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), routeTouch: (_, _, _, _) => routed++);

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: false);

        Assert.AreEqual(2, routed, "A tap routes both Down and Up outside edit mode");
    }

    [TestMethod]
    public void Feed_TapInEditMode_VetoesWidgetRouting()
    {
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), routeTouch: (_, _, _, _) => routed++);

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: true);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: true);

        Assert.AreEqual(0, routed, "Edit mode must veto widget routing for every source");
    }

    [TestMethod]
    public void Feed_SwipeInEditMode_StillNavigates()
    {
        int? navigated = null;
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0),
            navigateTo: i => navigated = i, routeTouch: (_, _, _, _) => routed++);

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: true);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: true);

        Assert.AreEqual(1, navigated, "Page actions must still apply in edit mode");
        Assert.AreEqual(0, routed);
    }

    // ── press orchestration: hit-test → select → begin-or-feed ──

    [TestMethod]
    public void Press_DesktopInEditModeOnWidget_BeginsManipulation()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var page = PageWith(widget);
        PlacedWidgetInstance? selected = null;
        int routed = 0;
        var controller = new InputController(
            () => StateOf(page, 0, count: 1),
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => widget,
            select: w => selected = w);

        controller.Press(60, 60, InputSource.DesktopEdit, editMode: true);

        Assert.AreSame(widget, selected, "A desktop press must select the hit widget");
        Assert.AreEqual(0, routed, "A manipulation press must not feed the gesture machine");
        // The press began a drag: the next move sample is consumed.
        Assert.IsTrue(controller.Move(160, 110, InputSource.DesktopEdit, editMode: true, out bool changed));
        Assert.IsTrue(changed);
        Assert.AreEqual(100f, widget.X, 0.01f);
    }

    [TestMethod]
    public void Press_DesktopInEditModeOnEmpty_SelectsNullAndFeedsWithVeto()
    {
        var page = TwoPages();
        PlacedWidgetInstance? selected = null;
        int routed = 0;
        var controller = new InputController(
            () => StateOf(page, 0),
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null,
            select: w => selected = w);

        controller.Press(60, 60, InputSource.DesktopEdit, editMode: true);

        Assert.IsNull(selected, "A press on empty canvas clears the selection");
        Assert.AreEqual(0, routed, "Edit mode must veto routing of the Down sample");
    }

    [TestMethod]
    public void Press_DesktopOutsideEditMode_RoutesToWidgets()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var page = PageWith(widget);
        int routed = 0;
        var controller = new InputController(
            () => StateOf(page, 0),
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => widget);

        controller.Press(100, 100, InputSource.DesktopEdit, editMode: false);

        Assert.AreEqual(1, routed, "Runtime-mode desktop input routes like device input");
    }

    [TestMethod]
    public void Press_Device_AlwaysRoutesEvenInEditMode()
    {
        var page = TwoPages();
        int routed = 0;
        PlacedWidgetInstance? selected = null;
        var controller = new InputController(
            () => StateOf(page, 0),
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null,
            select: w => selected = w);

        controller.Press(100, 100, InputSource.Device, editMode: true);

        Assert.AreEqual(1, routed, "Device touches are runtime input and must reach widgets");
        Assert.IsNull(selected, "A device press must never drive desktop selection");
    }

    [TestMethod]
    public void Release_AfterPlainPress_FeedsTouchUp()
    {
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(
            () => StateOf(page, 0),
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null);

        controller.Press(100, 100, InputSource.Device, editMode: false);
        bool wasManipulating = controller.Release(100, 100, InputSource.Device, editMode: false, out _);

        Assert.IsFalse(wasManipulating);
        Assert.AreEqual(2, routed, "A plain tap routes Down and Up");
    }

    [TestMethod]
    public void Move_WithoutManipulation_FeedsMachineInstead()
    {
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(
            () => StateOf(page, 0),
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null);

        controller.Press(100, 100, InputSource.Device, editMode: false);
        bool consumed = controller.Move(120, 120, InputSource.Device, editMode: false, out bool changed);

        Assert.IsFalse(consumed, "No manipulation in progress — the sample feeds the machine");
        Assert.IsFalse(changed);
        Assert.AreEqual(2, routed, "Down then Move both route");
    }

    // ── manipulation: decision ──────────────────────────────

    [TestMethod]
    public void Begin_OnWidgetInEditMode_StartsDrag()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        var kind = controller.BeginManipulation(widget, 60, 60, editMode: true);

        Assert.AreEqual(ManipulationKind.Drag, kind);
    }

    [TestMethod]
    public void Begin_OnResizeHandle_StartsResize()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        var kind = controller.BeginManipulation(widget, 400, 140, editMode: true);

        Assert.AreEqual(ManipulationKind.Resize, kind);
    }

    [TestMethod]
    public void Begin_WhenEditModeOff_ReturnsNone()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        var kind = controller.BeginManipulation(widget, 60, 60, editMode: false);

        Assert.AreEqual(ManipulationKind.None, kind);
    }

    [TestMethod]
    public void Begin_OnEmptyCanvas_ReturnsNone()
    {
        var controller = new InputController(() => StateOf(new PageLayout(), 0, count: 1));

        var kind = controller.BeginManipulation(null, 60, 60, editMode: true);

        Assert.AreEqual(ManipulationKind.None, kind);
    }

    // ── manipulation: drag / resize / snap ──────────────────

    [TestMethod]
    public void Move_Drag_UpdatesWidgetPosition()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));
        controller.BeginManipulation(widget, 60, 60, editMode: true);

        bool consumed = controller.MoveManipulation(widget, 160, 110, editMode: true, out bool changed);

        Assert.IsTrue(consumed);
        Assert.IsTrue(changed);
        Assert.AreEqual(100f, widget.X, 0.01f);
        Assert.AreEqual(50f, widget.Y, 0.01f);
    }

    [TestMethod]
    public void Move_WithoutManipulation_ReturnsFalse()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        bool consumed = controller.MoveManipulation(widget, 160, 110, editMode: true, out bool changed);

        Assert.IsFalse(consumed);
        Assert.IsFalse(changed);
    }

    [TestMethod]
    public void Move_Resize_UpdatesSizeWithMinimums()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));
        controller.BeginManipulation(widget, 400, 140, editMode: true);

        controller.MoveManipulation(widget, 20, 20, editMode: true, out _);

        Assert.AreEqual(40f, widget.Width, "Width must clamp to the minimum");
        Assert.AreEqual(30f, widget.Height, "Height must clamp to the minimum");
    }

    [TestMethod]
    public void End_DragWithSnapToGrid_SnapsWidget()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget, snapToGrid: true), 0, count: 1));
        controller.BeginManipulation(widget, 60, 60, editMode: true);
        controller.MoveManipulation(widget, 250, 160, editMode: true, out _);

        bool wasManipulating = controller.EndManipulation(widget, editMode: true, out _);

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
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));
        controller.BeginManipulation(widget, 60, 60, editMode: true);
        controller.MoveManipulation(widget, 250, 160, editMode: true, out _);

        controller.EndManipulation(widget, editMode: true, out _);

        Assert.AreEqual(190f, widget.X, 0.01f);
        Assert.AreEqual(100f, widget.Y, 0.01f);
    }

    [TestMethod]
    public void End_WithoutManipulation_ReturnsFalse()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        bool wasManipulating = controller.EndManipulation(widget, editMode: true, out _);

        Assert.IsFalse(wasManipulating);
    }

    // ── manipulation: icon grab (Hotkey widget) ─────────────

    [TestMethod]
    public void Begin_OnHotkeyIcon_StartsIconGrab()
    {
        string iconName = GriddyIcons.Names.First();
        var hotkey = new HotkeyButtonWidget { Icon = iconName };
        var widget = PlaceWidget(0, 0, 406, 148, hotkey);
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        var kind = controller.BeginManipulation(widget, 203, 46, editMode: true);

        Assert.AreEqual(ManipulationKind.IconGrab, kind);
    }

    [TestMethod]
    public void Begin_OnIconGrabCapabilityWidget_StartsIconGrab_WithoutConcreteType()
    {
        // The controller must drive icon grabs through the IWidgetIconGrab
        // capability — no widget-type branch in the App layer.
        var grab = new FakeIconGrabWidget();
        var widget = PlaceWidget(0, 0, 406, 148, grab);
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));

        var kind = controller.BeginManipulation(widget, 203, 46, editMode: true);

        Assert.AreEqual(ManipulationKind.IconGrab, kind);
        Assert.AreEqual(1, grab.HitTestCalls);
        controller.EndManipulation(widget, editMode: true, out _);
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
        var widget = PlaceWidget(0, 0, 406, 148, hotkey);
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1));
        // ApplyGrabMove persists through SetProperty → context, so the widget
        // needs a context that resolves the owning placed instance.
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(widget);
        var context = new PersistingContext(profile);
        hotkey.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        controller.BeginManipulation(widget, 203, 46, editMode: true);

        bool consumed = controller.MoveManipulation(widget, 253, 76, editMode: true, out bool changed);

        Assert.IsTrue(consumed);
        Assert.IsTrue(changed, "Icon offset must change when the grab moves");
        Assert.AreNotEqual(0, hotkey.IconOffsetX);
        Assert.AreNotEqual(0, hotkey.IconOffsetY);
        Assert.IsTrue(widget.PropertyValues.ContainsKey("IconOffsetX"), "PropertyValues must persist the offset");
        controller.EndManipulation(widget, editMode: true, out bool iconMoved);
        Assert.IsTrue(iconMoved);
    }

    [TestMethod]
    public void Feed_Tap_RequestsRenderAfterRouting()
    {
        int renders = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), requestRender: () => renders++);

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: false);

        Assert.AreEqual(2, renders, "Both routed samples request a canvas refresh");
    }

    // ── manipulation-outcome funnel ─────────────────────────

    [TestMethod]
    public void Move_ConsumedByDrag_ReportsChangedThroughFunnel()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var page = PageWith(widget);
        var changes = new List<ManipulationChange>();
        var controller = new InputController(
            () => StateOf(page, 0, count: 1),
            hitTest: (_, _, _) => widget,
            onManipulation: changes.Add);

        controller.Press(60, 60, InputSource.DesktopEdit, editMode: true);
        controller.Move(160, 110, InputSource.DesktopEdit, editMode: true, out _);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(changes[0].Changed, "A drag move must report Changed");
        Assert.IsFalse(changes[0].IconMoved, "Mid-grab icon moves are reported on release, not move");
    }

    [TestMethod]
    public void Release_EndingDrag_ReportsChangedThroughFunnel()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var page = PageWith(widget);
        var changes = new List<ManipulationChange>();
        var controller = new InputController(
            () => StateOf(page, 0, count: 1),
            hitTest: (_, _, _) => widget,
            onManipulation: changes.Add);

        controller.Press(60, 60, InputSource.DesktopEdit, editMode: true);
        controller.Move(160, 110, InputSource.DesktopEdit, editMode: true, out _);
        controller.Release(160, 110, InputSource.DesktopEdit, editMode: true, out _);

        Assert.AreEqual(2, changes.Count, "Move and release each report through the funnel");
        Assert.IsTrue(changes[1].Changed, "The release ends the manipulation and must persist/refresh");
        Assert.IsFalse(changes[1].IconMoved, "A plain drag moves no icon");
    }

    [TestMethod]
    public void Release_EndingIconGrab_ReportsIconMoved()
    {
        var hotkey = new HotkeyButtonWidget { Icon = GriddyIcons.Names.First() };
        var widget = PlaceWidget(0, 0, 406, 148, hotkey);
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(widget);
        var context = new PersistingContext(profile);
        hotkey.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

        var changes = new List<ManipulationChange>();
        var controller = new InputController(
            () => StateOf(PageWith(widget), 0, count: 1),
            hitTest: (_, _, _) => widget,
            onManipulation: changes.Add);

        controller.Press(203, 46, InputSource.DesktopEdit, editMode: true);
        controller.Move(253, 76, InputSource.DesktopEdit, editMode: true, out _);
        controller.Release(253, 76, InputSource.DesktopEdit, editMode: true, out _);

        Assert.IsTrue(changes[^1].IconMoved, "An icon grab that moved must report IconMoved on release");
        Assert.IsTrue(changes[^1].Changed);
    }

    [TestMethod]
    public void DeviceTouch_NeverReportsThroughFunnel()
    {
        var page = TwoPages();
        int funnelCalls = 0;
        var controller = new InputController(
            () => StateOf(page, 0),
            routeTouch: (_, _, _, _) => { },
            hitTest: (_, _, _) => null,
            onManipulation: _ => funnelCalls++);

        controller.Press(100, 100, InputSource.Device, editMode: false);
        controller.Move(120, 120, InputSource.Device, editMode: false, out _);
        controller.Release(120, 120, InputSource.Device, editMode: false, out _);

        Assert.AreEqual(0, funnelCalls, "Device touches never manipulate, so the funnel never fires");
    }
}
