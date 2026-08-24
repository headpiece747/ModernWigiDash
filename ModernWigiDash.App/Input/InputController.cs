using ModernWigiDash.App.Gestures;
using ModernWigiDash.Core.Rendering;
using SkiaSharp;

namespace ModernWigiDash.App.Input;

/// <summary>
/// What kind of edit-mode manipulation a mouse press started.
/// </summary>
internal enum ManipulationKind
{
    None,
    Drag,
    Resize,
    IconGrab
}

/// <summary>
/// Where an input sample came from. The veto is a property of the
/// <em>source</em>: the desktop's authoring input manipulates and vetoes
/// widget routing while the desktop is in edit mode (the controller reads it
/// live through its injected probe), while the physical display always routes
/// (hotkeys fire on the device even while the desktop is in edit mode).
/// </summary>
internal enum InputSource
{
    DesktopEdit,
    Device
}

/// <summary>
/// The page-state snapshot the gesture machine needs for page navigation and
/// touch routing. One accessor behind the controller instead of three
/// re-extractions per call at every feed site.
/// </summary>
internal readonly record struct InputState(PageLayout ActivePage, int PageCount, int ActivePageIndex);

/// <summary>
/// The single input module. Behind one small interface it owns:
///
///  • the gesture machine (<see cref="GestureInterpreter"/>) and the
///    application of its outcomes — page navigation, widget-touch routing,
///    and the edit-mode veto that suppresses routing for every source;
///  • edit-mode manipulation — the drag/resize/icon-grab decision and the
///    move/snap-to-grid math;
///  • the press orchestration — hit-test → select →
///    begin-manipulation-or-feed — so every input source (mouse, device)
///    crosses the same tested policy.
///
/// Callers feed the source-aware <see cref="Press"/>/<see cref="Move"/>/
/// <see cref="Release"/> surface with coordinates and the source only; the
/// controller reads the desktop's live edit mode itself (the injected
/// <paramref name="desktopEditMode"/> probe) and derives the veto from the
/// source, so the call sites never spell it. All page-switch UI work
/// (tab rebuild, selection, canvas refresh) stays in MainWindow behind the
/// <paramref name="navigateTo"/> and <paramref name="select"/> seams.
/// </summary>
internal sealed class InputController
{
    private readonly GestureInterpreter _machine = new();
    private readonly Func<InputState> _state;
    private readonly Func<bool> _desktopEditMode;
    private readonly Action<int>? _navigateTo;
    private readonly Action? _requestRender;
    private readonly Action<PageLayout, float, float, TouchEventType> _routeTouch;
    private readonly Func<PageLayout, float, float, PlacedWidgetInstance?> _hitTest;
    private readonly Action<PlacedWidgetInstance?>? _select;
    private readonly Action<ManipulationChange>? _onManipulation;

    private ManipulationKind _manipulation;
    private PlacedWidgetInstance? _manipulationTarget;
    private Point _lastPos;
    private Point _iconGrabOffset;
    private bool _iconGrabMoved;

    /// <param name="stateProvider">The page-state snapshot (active page, page
    /// count, active index) — MainWindow binds it to the profile once.</param>
    /// <param name="desktopEditMode">The live edit-mode read for the desktop.
    /// The veto is a property of the source: the desktop's authoring input
    /// manipulates and vetoes widget routing while this read is true, and the
    /// device is runtime input that always routes, so the probe is only ever
    /// read for desktop samples. The read lives inside the controller, not at
    /// the call sites, so a sample's edit mode is decided once per sample.</param>
    /// <param name="navigateTo">Page-switch seam. Called with the target page
    /// index when a swipe/arrow-tap navigates; MainWindow performs the UI work.</param>
    /// <param name="requestRender">Canvas refresh seam, invoked when a touch
    /// sample was routed to a widget.</param>
    /// <param name="routeTouch">Widget-touch routing; defaults to
    /// <see cref="WidgetRouting"/>'s hit-test routing. Tests inject a spy.</param>
    /// <param name="hitTest">Widget hit-testing; defaults to
    /// <see cref="WidgetRouting"/>'s. Tests inject a fake page.</param>
    /// <param name="select">Selection seam; MainWindow selects the hit widget
    /// and refreshes the inspector/canvas. Tests inject a spy.</param>
    /// <param name="onManipulation">One refresh funnel: invoked whenever an
    /// edit-mode manipulation changes a widget or ends (including the release
    /// snap-to-grid), with what changed. MainWindow binds one handler here so
    /// the refresh-after-manipulation policy is declared once instead of being
    /// re-derived at every mouse call site. The device-touch path never
    /// manipulates, so it never fires this.</param>
    public InputController(
        Func<InputState> stateProvider,
        Func<bool> desktopEditMode,
        Action<int>? navigateTo = null,
        Action? requestRender = null,
        Action<PageLayout, float, float, TouchEventType>? routeTouch = null,
        Func<PageLayout, float, float, PlacedWidgetInstance?>? hitTest = null,
        Action<PlacedWidgetInstance?>? select = null,
        Action<ManipulationChange>? onManipulation = null)
    {
        _state = stateProvider;
        _desktopEditMode = desktopEditMode;
        _navigateTo = navigateTo;
        _requestRender = requestRender;
        _routeTouch = routeTouch ?? WidgetRouting.RouteTouch;
        _hitTest = hitTest ?? WidgetRouting.HitTest;
        _select = select;
        _onManipulation = onManipulation;
    }

    /// <summary>
    /// Feeds one normalized input sample. Applies the gesture machine's
    /// decision: page navigation wins, otherwise the sample is routed to
    /// widgets — unless <paramref name="suppressWidgetRouting"/> is set, in
    /// which case routing is vetoed (page actions still apply). The suppression
    /// flag is derived by the source-aware <see cref="Press"/>/<see cref="Move"/>
    /// /<see cref="Release"/> surface; direct calls (tests) pass it explicitly.
    /// </summary>
    internal void Feed(TouchEventType type, float x, float y, bool suppressWidgetRouting)
    {
        InputState state = _state();
        var outcome = _machine.Feed(type, x, y, state.PageCount, state.ActivePageIndex);

        switch (outcome.PageAction)
        {
            case GesturePageAction.NextPage:
                _navigateTo?.Invoke(state.ActivePageIndex + 1);
                return;
            case GesturePageAction.PrevPage:
                _navigateTo?.Invoke(state.ActivePageIndex - 1);
                return;
        }

        if (outcome.RouteToWidgets && !suppressWidgetRouting)
        {
            _routeTouch(state.ActivePage, x, y, outcome.WidgetTouchType);
            _requestRender?.Invoke();
        }
    }

    /// <summary>
    /// One press sample from any source. The device always routes (runtime
    /// input). Desktop presses hit-test, select, and either start an edit-mode
    /// manipulation or feed the gesture machine — the orchestration that used
    /// to be duplicated across the window's mouse handlers.
    /// </summary>
    public void Press(float x, float y, InputSource source)
    {
        if (source == InputSource.Device)
        {
            Feed(TouchEventType.TouchDown, x, y, suppressWidgetRouting: false);
            return;
        }

        bool editMode = EditModeFor(source);
        InputState state = _state();
        var hit = _hitTest(state.ActivePage, x, y);
        _select?.Invoke(hit);
        _manipulationTarget = hit;

        if (BeginManipulation(hit, x, y, editMode) == ManipulationKind.None)
        {
            Feed(TouchEventType.TouchDown, x, y, suppressWidgetRouting: editMode);
        }
    }

    /// <summary>
    /// One move sample. An in-progress manipulation consumes it; otherwise the
    /// sample feeds the gesture machine with the source's suppression rule.
    /// </summary>
    /// <returns>True when the sample was consumed by a manipulation (the caller
    /// should refresh the inspector and canvas when <paramref name="changed"/>);
    /// false when it fed the gesture machine instead.</returns>
    public bool Move(float x, float y, InputSource source, out bool changed)
    {
        changed = false;
        bool editMode = EditModeFor(source);
        if (MoveManipulation(_manipulationTarget, x, y, editMode, out changed))
        {
            if (changed)
            {
                // Icon-moved mid-grab is deliberately not reported here: the
                // window refreshes the inspector once, on release (the release
                // outcome carries IconMoved).
                _onManipulation?.Invoke(new ManipulationChange(Changed: true, IconMoved: false));
            }
            return true;
        }

        Feed(TouchEventType.TouchMove, x, y, suppressWidgetRouting: editMode);
        return false;
    }

    /// <summary>
    /// One release sample. Ends any in-progress manipulation; a plain release
    /// feeds the gesture machine's TouchUp (which decides swipes and taps).
    /// </summary>
    /// <returns>True when a manipulation was in progress (the caller skips
    /// gesture handling and refreshes); <paramref name="iconMoved"/> reports
    /// whether an icon grab changed the widget's icon offsets.</returns>
    public bool Release(float x, float y, InputSource source, out bool iconMoved)
    {
        bool editMode = EditModeFor(source);
        bool wasManipulating = EndManipulation(_manipulationTarget, editMode, out iconMoved);
        _manipulationTarget = null;

        if (wasManipulating)
        {
            // The release is where snap-to-grid lands: the funnel fires with
            // Changed=true so the caller persists and refreshes once.
            _onManipulation?.Invoke(new ManipulationChange(Changed: true, IconMoved: iconMoved));
        }
        else
        {
            Feed(TouchEventType.TouchUp, x, y, suppressWidgetRouting: editMode);
        }

        return wasManipulating;
    }

    /// <summary>
    /// Starts an edit-mode manipulation for a press on <paramref name="hit"/>.
    /// Returns <see cref="ManipulationKind.None"/> when the press is not a
    /// manipulation (no widget, or edit mode off) — the caller then feeds the
    /// gesture machine via <see cref="Feed"/>.
    /// </summary>
    internal ManipulationKind BeginManipulation(
        PlacedWidgetInstance? hit,
        float x,
        float y,
        bool editMode)
    {
        _manipulation = ManipulationKind.None;
        _iconGrabMoved = false;
        _lastPos = new Point(x, y);

        if (hit is null || !editMode)
            return _manipulation;

        // Click in the resize handle (bottom-right corner) resizes. The handle
        // size is owned by the routing module that shapes the hit test —
        // one source of truth.
        if (x >= hit.X + hit.Width - WidgetRouting.ResizeHandleSize &&
            y >= hit.Y + hit.Height - WidgetRouting.ResizeHandleSize)
        {
            _manipulation = ManipulationKind.Resize;
        }
        else if (hit.ActiveInstance is IWidgetIconGrab grab)
        {
            // Hit-test in the widget's rotated-local space — the same geometry
            // its icon is drawn in (ToLocalPoint is the render-transform
            // inverse), so a rotated icon's grab region matches its footprint.
            SKPoint local = hit.ToLocalPoint(x, y);
            if (grab.IsPointOverIcon(hit.Width, hit.Height, local.X, local.Y))
            {
                _manipulation = ManipulationKind.IconGrab;
                if (grab.TryGetIconCenter(hit.Width, hit.Height, out var iconCenter, out _))
                    _iconGrabOffset = new Point(iconCenter.X - local.X, iconCenter.Y - local.Y);
            }
            else
            {
                _manipulation = ManipulationKind.Drag;
            }
        }
        else
        {
            _manipulation = ManipulationKind.Drag;
        }

        return _manipulation;
    }

    /// <summary>
    /// Applies one move sample of an in-progress manipulation.
    /// </summary>
    /// <returns>True when the sample was consumed by a manipulation (the caller
    /// should refresh the inspector and canvas); false when no manipulation is
    /// active — the caller feeds <see cref="Feed"/> with TouchMove instead.</returns>
    internal bool MoveManipulation(PlacedWidgetInstance? widget, float x, float y, bool editMode, out bool changed)
    {
        changed = false;
        if (widget is null || !editMode || _manipulation == ManipulationKind.None)
            return false;

        float dx = x - (float)_lastPos.X;
        float dy = y - (float)_lastPos.Y;
        _lastPos = new Point(x, y);

        switch (_manipulation)
        {
            case ManipulationKind.Resize:
                // Usability floors so the resize handles stay grabbable — a
                // distinct policy from the inspector's typed-value validation
                // floor; both live in WidgetSizeLimits, so the floors can
                // never drift apart.
                widget.Width = Math.Max(WidgetSizeLimits.MinDragSizeX, x - widget.X);
                widget.Height = Math.Max(WidgetSizeLimits.MinDragSizeY, y - widget.Y);
                changed = true;
                return true;

            case ManipulationKind.Drag:
                widget.X += dx;
                widget.Y += dy;
                changed = true;
                return true;

            case ManipulationKind.IconGrab when widget.ActiveInstance is IWidgetIconGrab grab:
                changed = ApplyIconGrabMove(grab, widget, x, y);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Ends an in-progress manipulation: applies snap-to-grid for drag/resize
    /// and resets the manipulation state.
    /// </summary>
    /// <returns>True when a manipulation was in progress (the caller skips the
    /// gesture TouchUp feed); <paramref name="iconMoved"/> reports whether an
    /// icon grab changed the widget's icon offsets.</returns>
    internal bool EndManipulation(PlacedWidgetInstance? widget, bool editMode, out bool iconMoved)
    {
        iconMoved = _iconGrabMoved;

        bool wasManipulating = _manipulation != ManipulationKind.None;
        if (wasManipulating && widget is not null && editMode && _state().ActivePage.SnapToGrid &&
            _manipulation is ManipulationKind.Drag or ManipulationKind.Resize)
        {
            widget.X = GridSizeExtensions.SnapX(widget.X);
            widget.Y = GridSizeExtensions.SnapY(widget.Y);
            if (_manipulation == ManipulationKind.Resize)
            {
                widget.Width = GridSizeExtensions.SnapToCell(widget.Width, GridSizeExtensions.CellWidth);
                widget.Height = GridSizeExtensions.SnapToCell(widget.Height, GridSizeExtensions.CellHeight);
            }
        }

        _manipulation = ManipulationKind.None;
        _iconGrabMoved = false;
        return wasManipulating;
    }

    /// <summary>
    /// The edit-mode veto for one sample, derived from the source: the
    /// desktop reads its live probe (authoring input — manipulations active,
    /// widget routing vetoed while on), and the device is runtime input that
    /// never reads the probe, so the "the device always routes" fact lives
    /// here and nowhere else.
    /// </summary>
    private bool EditModeFor(InputSource source)
        => source == InputSource.DesktopEdit && _desktopEditMode();

    private bool ApplyIconGrabMove(IWidgetIconGrab grab, PlacedWidgetInstance widget, float x, float y)
    {
        // Pointer in the widget's rotated-local space, consistent with the
        // grab offset captured in Begin.
        SKPoint local = widget.ToLocalPoint(x, y);
        if (!grab.ApplyGrabMove(widget, local.X, local.Y, (float)_iconGrabOffset.X, (float)_iconGrabOffset.Y))
            return false;

        _iconGrabMoved = true;
        return true;
    }
}
