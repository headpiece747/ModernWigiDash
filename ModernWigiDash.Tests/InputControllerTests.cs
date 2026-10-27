using ModernWigiDash.App.Input;

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
        var controller = new InputController(() => StateOf(page, 0), () => false, navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: false);

        Assert.AreEqual(1, navigated);
    }

    [TestMethod]
    public void Feed_SwipeRight_NavigatesToPreviousPage()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 1), () => false, navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 550, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 800, 300, suppressWidgetRouting: false);

        Assert.AreEqual(0, navigated);
    }

    [TestMethod]
    public void Feed_ArrowRightTap_DoesNotNavigate()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), () => false, navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 990, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 990, 300, suppressWidgetRouting: false);

        Assert.IsNull(navigated);
    }

    [TestMethod]
    public void Feed_ArrowLeftTap_DoesNotNavigate()
    {
        int? navigated = null;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 1), () => false, navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 30, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 30, 300, suppressWidgetRouting: false);

        Assert.IsNull(navigated);
    }

    [TestMethod]
    public void Feed_SwipeLeftAtLastPage_DoesNotNavigate()
    {
        int navigations = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 1), () => false, navigateTo: _ => navigations++);

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
        var controller = new InputController(() => StateOf(page, 0), () => false, routeTouch: (_, _, _, _) => routed++);

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: false);

        Assert.AreEqual(2, routed, "A tap routes both Down and Up outside edit mode");
    }

    [TestMethod]
    public void Feed_TapInEditMode_VetoesWidgetRouting()
    {
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), () => true, routeTouch: (_, _, _, _) => routed++);

        controller.Feed(TouchEventType.TouchDown, 100, 100, suppressWidgetRouting: true);
        controller.Feed(TouchEventType.TouchUp, 100, 100, suppressWidgetRouting: true);

        Assert.AreEqual(0, routed, "Edit mode must veto widget routing for every source");
    }

    [TestMethod]
    public void DeviceSamples_NeverReadTheDesktopEditModeProbe()
    {
        var page = TwoPages();
        int routed = 0;
        var controller = new InputController(
            () => StateOf(page, 0),
            () => throw new InvalidOperationException("Device input must not read the desktop edit-mode probe"),
            routeTouch: (_, _, _, _) => routed++);

        controller.Press(100, 100, InputSource.Device);
        controller.Move(120, 120, InputSource.Device, out _);
        controller.Release(120, 120, InputSource.Device, out _);

        Assert.AreEqual(3, routed, "Device Down/Move/Up all route without touching the probe");
    }

    [TestMethod]
    public void DesktopEditMode_IsReadPerSample_NotCachedAtConstruction()
    {
        bool editMode = false;
        var page = TwoPages();
        int routed = 0;
        var controller = new InputController(
            () => StateOf(page, 0),
            () => editMode,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null);

        controller.Press(100, 100, InputSource.DesktopEdit);
        Assert.AreEqual(1, routed, "Runtime-mode desktop input routes like device input");

        editMode = true;
        controller.Press(100, 100, InputSource.DesktopEdit);
        Assert.AreEqual(1, routed, "Edit mode read live per sample must veto the next press");
    }

    [TestMethod]
    public void Feed_SwipeInEditMode_StillNavigates()
    {
        int? navigated = null;
        int routed = 0;
        var page = TwoPages();
        var controller = new InputController(() => StateOf(page, 0), () => true,
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
            () => true,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => widget,
            select: w => selected = w);

        controller.Press(60, 60, InputSource.DesktopEdit);

        Assert.AreSame(widget, selected, "A desktop press must select the hit widget");
        Assert.AreEqual(0, routed, "A manipulation press must not feed the gesture machine");
        // The press began a drag: the next move sample is consumed.
        Assert.IsTrue(controller.Move(160, 110, InputSource.DesktopEdit, out bool changed));
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
            () => true,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null,
            select: w => selected = w);

        controller.Press(60, 60, InputSource.DesktopEdit);

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
            () => false,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => widget);

        controller.Press(100, 100, InputSource.DesktopEdit);

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
            () => true,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null,
            select: w => selected = w);

        controller.Press(100, 100, InputSource.Device);

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
            () => false,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null);

        controller.Press(100, 100, InputSource.Device);
        bool wasManipulating = controller.Release(100, 100, InputSource.Device, out _);

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
            () => false,
            routeTouch: (_, _, _, _) => routed++,
            hitTest: (_, _, _) => null);

        controller.Press(100, 100, InputSource.Device);
        bool consumed = controller.Move(120, 120, InputSource.Device, out bool changed);

        Assert.IsFalse(consumed, "No manipulation in progress — the sample feeds the machine");
        Assert.IsFalse(changed);
        Assert.AreEqual(2, routed, "Down then Move both route");
    }

    // ── manipulation: decision ──────────────────────────────

    [TestMethod]
    public void Begin_OnWidgetInEditMode_StartsDrag()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

        var kind = controller.BeginManipulation(widget, 60, 60, editMode: true);

        Assert.AreEqual(ManipulationKind.Drag, kind);
    }

    [TestMethod]
    public void Begin_OnResizeHandle_StartsResize()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

        var kind = controller.BeginManipulation(widget, 400, 140, editMode: true);

        Assert.AreEqual(ManipulationKind.Resize, kind);
    }

    [TestMethod]
    public void Begin_WhenEditModeOff_ReturnsNone()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

        var kind = controller.BeginManipulation(widget, 60, 60, editMode: false);

        Assert.AreEqual(ManipulationKind.None, kind);
    }

    [TestMethod]
    public void Begin_OnEmptyCanvas_ReturnsNone()
    {
        var controller = new InputController(() => StateOf(new PageLayout(), 0, count: 1), () => true);

        var kind = controller.BeginManipulation(null, 60, 60, editMode: true);

        Assert.AreEqual(ManipulationKind.None, kind);
    }

    // ── manipulation: drag / resize / snap ──────────────────

    [TestMethod]
    public void Move_Drag_UpdatesWidgetPosition()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);
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
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

        bool consumed = controller.MoveManipulation(widget, 160, 110, editMode: true, out bool changed);

        Assert.IsFalse(consumed);
        Assert.IsFalse(changed);
    }

    [TestMethod]
    public void Move_Resize_UpdatesSizeWithMinimums()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);
        controller.BeginManipulation(widget, 400, 140, editMode: true);

        controller.MoveManipulation(widget, 20, 20, editMode: true, out _);

        Assert.AreEqual(40f, widget.Width, "Width must clamp to the minimum");
        Assert.AreEqual(30f, widget.Height, "Height must clamp to the minimum");
    }

    [TestMethod]
    public void End_DragWithSnapToGrid_SnapsWidget()
    {
        var widget = PlaceWidget(0, 0, 406, 148, new TestWidget());
        var controller = new InputController(() => StateOf(PageWith(widget, snapToGrid: true), 0, count: 1), () => true);
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
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);
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
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

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
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

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
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);

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
        var controller = new InputController(() => StateOf(PageWith(widget), 0, count: 1), () => true);
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
        var controller = new InputController(() => StateOf(page, 0), () => false, requestRender: () => renders++);

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
            () => true,
            hitTest: (_, _, _) => widget,
            onManipulation: changes.Add);

        controller.Press(60, 60, InputSource.DesktopEdit);
        controller.Move(160, 110, InputSource.DesktopEdit, out _);

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
            () => true,
            hitTest: (_, _, _) => widget,
            onManipulation: changes.Add);

        controller.Press(60, 60, InputSource.DesktopEdit);
        controller.Move(160, 110, InputSource.DesktopEdit, out _);
        controller.Release(160, 110, InputSource.DesktopEdit, out _);

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
            () => true,
            hitTest: (_, _, _) => widget,
            onManipulation: changes.Add);

        controller.Press(203, 46, InputSource.DesktopEdit);
        controller.Move(253, 76, InputSource.DesktopEdit, out _);
        controller.Release(253, 76, InputSource.DesktopEdit, out _);

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
            () => false,
            routeTouch: (_, _, _, _) => { },
            hitTest: (_, _, _) => null,
            onManipulation: _ => funnelCalls++);

        controller.Press(100, 100, InputSource.Device);
        controller.Move(120, 120, InputSource.Device, out _);
        controller.Release(120, 120, InputSource.Device, out _);

        Assert.AreEqual(0, funnelCalls, "Device touches never manipulate, so the funnel never fires");
    }


    private sealed class TrackingWidget : ModernWidgetBase
    {
        public List<TouchEventType> ReceivedEvents { get; } = [];
        public override void OnTouch(SKPoint localPoint, TouchEventType eventType) => ReceivedEvents.Add(eventType);
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    private sealed class ClaimingWidget : ModernWidgetBase, IGestureClaimant
    {
        public bool ClaimsHorizontalDrag => true;
        public List<TouchEventType> ReceivedEvents { get; } = [];
        public override void OnTouch(SKPoint localPoint, TouchEventType eventType) => ReceivedEvents.Add(eventType);
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    [TestMethod]
    public void Feed_Swipe_SynthesizesTouchCancelToCapturedWidget()
    {
        var tracker = new TrackingWidget();
        var placed = PlaceWidget(0, 0, 500, 500, tracker);
        var page = PageWith(placed);
        int? navigated = null;

        var controller = new InputController(
            () => StateOf(page, 0, count: 2),
            () => false,
            navigateTo: i => navigated = i);

        // Press down on the widget, then swipe left exceeding threshold
        controller.Feed(TouchEventType.TouchDown, 300, 200, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 100, 200, suppressWidgetRouting: false);

        Assert.AreEqual(1, navigated, "Swipe must trigger page navigation");
        CollectionAssert.AreEqual(
            new[] { TouchEventType.TouchDown, TouchEventType.TouchCancel },
            tracker.ReceivedEvents,
            "Captured widget must receive TouchCancel on swipe promotion, never TouchUp");
    }

    [TestMethod]
    public void Feed_GestureClaimantWidget_SuppressesPageSwipe()
    {
        var claimer = new ClaimingWidget();
        var placed = PlaceWidget(0, 0, 500, 500, claimer);
        var page = PageWith(placed);
        int? navigated = null;

        var controller = new InputController(
            () => StateOf(page, 0, count: 2),
            () => false,
            navigateTo: i => navigated = i);

        // Press down on claiming widget and drag horizontally > 70px
        controller.Feed(TouchEventType.TouchDown, 300, 200, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 100, 200, suppressWidgetRouting: false);

        Assert.IsNull(navigated, "Claimant widget must suppress page swipe navigation");
        CollectionAssert.AreEqual(
            new[] { TouchEventType.TouchDown, TouchEventType.TouchUp },
            claimer.ReceivedEvents,
            "Claimant widget must receive TouchUp since swipe was suppressed");
    }

    private sealed class FakeInputHost : IInputHostContext
    {
        private readonly InputState _state;
        public int NavigatedTo { get; private set; } = -1;
        public int Renders { get; private set; }

        public FakeInputHost(InputState state) => _state = state;

        public InputState GetState() => _state;
        public bool IsDesktopEditMode() => false;
        public void NavigateTo(int pageIndex) => NavigatedTo = pageIndex;
        public void RequestRender() => Renders++;
        public void RouteTouch(PageLayout page, float x, float y, TouchEventType eventType) { }
        public PlacedWidgetInstance? HitTest(PageLayout page, float x, float y) => null;
        public void SelectWidget(PlacedWidgetInstance? widget) { }
        public void OnManipulation(ManipulationChange change) { }
    }

    [TestMethod]
    public void InputController_HostContextConstructor_OperatesCorrectly()
    {
        var page = TwoPages();
        var host = new FakeInputHost(StateOf(page, 0, count: 2));
        var controller = new InputController(host);

        controller.Feed(TouchEventType.TouchDown, 800, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 550, 300, suppressWidgetRouting: false);

        Assert.AreEqual(1, host.NavigatedTo, "InputController instantiated via IInputHostContext navigates cleanly");
    }

    [TestMethod]
    public void Feed_DragOutsideWidget_StillDeliversTouchUpToCapturedWidget()
    {
        var tracker = new TrackingWidget();
        var placed = PlaceWidget(0, 0, 100, 100, tracker);
        var page = PageWith(placed);

        var controller = new InputController(
            () => StateOf(page, 0, count: 2),
            () => false);

        // TouchDown inside widget at (50, 50)
        controller.Feed(TouchEventType.TouchDown, 50, 50, suppressWidgetRouting: false);
        // Move outside widget to (150, 150)
        controller.Feed(TouchEventType.TouchMove, 150, 150, suppressWidgetRouting: false);
        // Release outside widget at (150, 150)
        controller.Feed(TouchEventType.TouchUp, 150, 150, suppressWidgetRouting: false);

        CollectionAssert.AreEqual(
            new[] { TouchEventType.TouchDown, TouchEventType.TouchMove, TouchEventType.TouchUp },
            tracker.ReceivedEvents,
            "Captured widget must receive TouchUp even when released outside its bounds");
    }

    [TestMethod]
    public void Feed_IntermediateHardwareDownPoints_DoNotOverwriteCapturedWidget()
    {
        var tracker = new TrackingWidget();
        var placed = PlaceWidget(0, 0, 100, 100, tracker);
        var page = PageWith(placed);
        int? navigated = null;

        var controller = new InputController(
            () => StateOf(page, 1, count: 2),
            () => false,
            navigateTo: i => navigated = i);

        // First Down inside widget at (50, 50)
        controller.Feed(TouchEventType.TouchDown, 50, 50, suppressWidgetRouting: false);
        // Intermediate hardware Down points (hardware protocol sends Down for movements)
        controller.Feed(TouchEventType.TouchDown, 120, 50, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchDown, 180, 50, suppressWidgetRouting: false);
        // Release causing swipe (50 to 200 = delta 150 > 70 => PrevPage from page 1 to 0)
        controller.Feed(TouchEventType.TouchUp, 200, 50, suppressWidgetRouting: false);

        Assert.AreEqual(0, navigated, "Swipe must navigate to page 0");
        CollectionAssert.Contains(
            tracker.ReceivedEvents,
            TouchEventType.TouchCancel,
            "Widget captured on initial Down must receive TouchCancel even after intermediate hardware Down points");
    }

    [TestMethod]
    public void Feed_EdgeSwipe_NavigatesPageEvenOnClaimingWidget()
    {
        var claimer = new ClaimingWidget();
        var placed = PlaceWidget(0, 0, 1016, 592, claimer);
        var page = PageWith(placed);
        int? navigated = null;

        // On page 1: edge swipe from left edge (X=20 < EdgeSwipeThreshold=40) towards right (X=150)
        var controller = new InputController(
            () => StateOf(page, 1, count: 2),
            () => false,
            navigateTo: i => navigated = i);

        controller.Feed(TouchEventType.TouchDown, 20, 300, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 150, 300, suppressWidgetRouting: false);

        Assert.AreEqual(0, navigated, "Edge swipe must navigate to previous page even on full-screen claimant widget");
    }

    [TestMethod]
    public void Feed_PressEmptySpace_DragReleaseOverWidget_DoesNotDeliverTouchUpToWidget()
    {
        var tracker = new TrackingWidget();
        var placed = PlaceWidget(200, 200, 100, 100, tracker);
        var page = PageWith(placed);

        var controller = new InputController(
            () => StateOf(page, 0, count: 2),
            () => false);

        // Press down on empty space at (50, 50)
        controller.Feed(TouchEventType.TouchDown, 50, 50, suppressWidgetRouting: false);
        // Drag over the widget and release at (250, 250)
        controller.Feed(TouchEventType.TouchMove, 250, 250, suppressWidgetRouting: false);
        controller.Feed(TouchEventType.TouchUp, 250, 250, suppressWidgetRouting: false);

        Assert.AreEqual(0, tracker.ReceivedEvents.Count,
            "Widget must never receive touch events from a gesture that originated outside it");
    }

}
