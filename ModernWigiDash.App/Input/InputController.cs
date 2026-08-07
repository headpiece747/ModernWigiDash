using System.Windows;
using ModernWigiDash.App.Gestures;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.App.Input;

/// <summary>
/// What kind of edit-mode manipulation a mouse press started.
/// </summary>
public enum ManipulationKind
{
    None,
    Drag,
    Resize,
    IconGrab
}

/// <summary>
/// The single input module. Behind one small interface it owns:
///
///  • the gesture machine (<see cref="GestureInterpreter"/>) and the
///    application of its outcomes — page navigation, widget-touch routing,
///    and the edit-mode veto that suppresses routing for every source;
///  • edit-mode manipulation — the drag/resize/icon-grab decision and the
///    move/snap-to-grid math that used to live in MainWindow's mouse handlers.
///
/// Callers — mouse handlers, hardware touch, WCF touch — only feed
/// Down/Move/Up coordinates plus an edit-mode flag and the page state. All
/// page-switch UI work (tab rebuild, selection, canvas refresh) stays in
/// MainWindow behind the <paramref name="navigateTo"/> seam.
/// </summary>
public sealed class InputController
{
    private readonly GestureInterpreter _machine;
    private readonly Action<int>? _navigateTo;
    private readonly Action? _requestRender;
    private readonly Action<PageLayout, float, float, TouchEventType> _routeTouch;

    private ManipulationKind _manipulation;
    private Point _lastPos;
    private Point _iconGrabOffset;
    private bool _iconGrabMoved;

    /// <param name="machine">Gesture machine; the controller creates one when
    /// omitted. Tests inject a machine to observe Feed calls.</param>
    /// <param name="navigateTo">Page-switch seam. Called with the target page
    /// index when a swipe/arrow-tap navigates; MainWindow performs the UI work.</param>
    /// <param name="requestRender">Canvas refresh seam, invoked when a touch
    /// sample was routed to a widget.</param>
    /// <param name="routeTouch">Widget-touch routing; defaults to the
    /// compositor's hit-test routing. Tests inject a spy.</param>
    public InputController(
        GestureInterpreter? machine = null,
        Action<int>? navigateTo = null,
        Action? requestRender = null,
        Action<PageLayout, float, float, TouchEventType>? routeTouch = null)
    {
        _machine = machine ?? new GestureInterpreter();
        _navigateTo = navigateTo;
        _requestRender = requestRender;
        _routeTouch = routeTouch ?? SkiaFrameCompositor.RouteTouch;
    }

    /// <summary>
    /// Feeds one normalized input sample from any source. Applies the gesture
    /// machine's decision: page navigation wins, otherwise the sample is routed
    /// to widgets — unless <paramref name="suppressWidgetRouting"/> is set, in
    /// which case routing is vetoed (page actions still apply).
    ///
    /// The suppression flag is a property of the <em>source</em>, not the
    /// display: the mouse passes the desktop edit-mode flag (authoring input —
    /// presses start manipulations instead of routing), while the physical
    /// display passes false — display touches are runtime input and must always
    /// reach widgets (hotkeys fire on the device even while the desktop is in
    /// edit mode).
    /// </summary>
    public void Feed(
        TouchEventType type,
        float x,
        float y,
        bool suppressWidgetRouting,
        int pageCount,
        int activePageIndex,
        PageLayout activePage)
    {
        var outcome = _machine.Feed(type, x, y, pageCount, activePageIndex);

        switch (outcome.PageAction)
        {
            case GesturePageAction.NextPage:
                _navigateTo?.Invoke(activePageIndex + 1);
                return;
            case GesturePageAction.PrevPage:
                _navigateTo?.Invoke(activePageIndex - 1);
                return;
        }

        if (outcome.RouteToWidgets && !suppressWidgetRouting)
        {
            _routeTouch(activePage, x, y, outcome.WidgetTouchType);
            _requestRender?.Invoke();
        }
    }

    /// <summary>
    /// Starts an edit-mode manipulation for a press on <paramref name="hit"/>.
    /// Returns <see cref="ManipulationKind.None"/> when the press is not a
    /// manipulation (no widget, or edit mode off) — the caller then feeds the
    /// gesture machine via <see cref="Feed"/>.
    /// </summary>
    public ManipulationKind Begin(
        PlacedWidgetInstance? hit,
        PlacedWidgetInstance? selected,
        float x,
        float y,
        bool editMode)
    {
        _manipulation = ManipulationKind.None;
        _iconGrabMoved = false;
        _lastPos = new Point(x, y);

        if (hit == null || !editMode)
            return _manipulation;

        // Click in the resize handle (bottom-right corner) resizes. The handle
        // size is owned by the compositor that draws it — one source of truth.
        if (hit == selected &&
            x >= hit.X + hit.Width - SkiaFrameCompositor.ResizeHandleSize &&
            y >= hit.Y + hit.Height - SkiaFrameCompositor.ResizeHandleSize)
        {
            _manipulation = ManipulationKind.Resize;
        }
        else if (hit.ActiveInstance is HotkeyButtonWidget hotkeyWidget &&
                 IsPointOverWidgetIcon(hotkeyWidget, hit.Width, hit.Height,
                     x - hit.X, y - hit.Y))
        {
            _manipulation = ManipulationKind.IconGrab;
            if (TryGetWidgetIconCenter(hotkeyWidget, hit.Width, hit.Height, out var iconCenter, out _))
                _iconGrabOffset = new Point(iconCenter.X - (x - hit.X), iconCenter.Y - (y - hit.Y));
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
    public bool Move(PlacedWidgetInstance? widget, float x, float y, bool editMode, out bool changed)
    {
        changed = false;
        if (widget == null || !editMode || _manipulation == ManipulationKind.None)
            return false;

        float dx = x - (float)_lastPos.X;
        float dy = y - (float)_lastPos.Y;
        _lastPos = new Point(x, y);

        switch (_manipulation)
        {
            case ManipulationKind.Resize:
                widget.Width = Math.Max(40f, x - widget.X);
                widget.Height = Math.Max(30f, y - widget.Y);
                changed = true;
                return true;

            case ManipulationKind.Drag:
                widget.X += dx;
                widget.Y += dy;
                changed = true;
                return true;

            case ManipulationKind.IconGrab when widget.ActiveInstance is HotkeyButtonWidget iconHotkey:
                changed = ApplyIconGrabMove(iconHotkey, widget, x, y);
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
    public bool End(PlacedWidgetInstance? widget, bool editMode, bool snapToGrid, out bool iconMoved)
    {
        iconMoved = _iconGrabMoved;

        bool wasManipulating = _manipulation != ManipulationKind.None;
        if (wasManipulating && widget != null && editMode && snapToGrid &&
            _manipulation is ManipulationKind.Drag or ManipulationKind.Resize)
        {
            widget.X = (float)Math.Round(widget.X / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth;
            widget.Y = (float)Math.Round(widget.Y / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight;
            if (_manipulation == ManipulationKind.Resize)
            {
                widget.Width = (float)Math.Round(widget.Width / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth;
                widget.Height = (float)Math.Round(widget.Height / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight;
            }
        }

        _manipulation = ManipulationKind.None;
        _iconGrabMoved = false;
        return wasManipulating;
    }

    private bool ApplyIconGrabMove(HotkeyButtonWidget hotkey, PlacedWidgetInstance widget, float x, float y)
    {
        float localX = x - widget.X;
        float localY = y - widget.Y;
        if (!TryGetWidgetIconCenter(hotkey, widget.Width, widget.Height, out _, out float half))
            return false;

        float cx = Math.Clamp(localX + (float)_iconGrabOffset.X, half, widget.Width - half);
        float cy = Math.Clamp(localY + (float)_iconGrabOffset.Y, half, widget.Height - half);
        int newX = (int)Math.Round(cx - widget.Width / 2f);
        int newY = (int)Math.Round(cy - widget.Height * 0.31f);
        if (newX == hotkey.IconOffsetX && newY == hotkey.IconOffsetY)
            return false;

        _iconGrabMoved = true;
        hotkey.IconOffsetX = newX;
        hotkey.IconOffsetY = newY;
        hotkey.OnPropertyChanged(nameof(HotkeyButtonWidget.IconOffsetX), newX);
        hotkey.OnPropertyChanged(nameof(HotkeyButtonWidget.IconOffsetY), newY);
        widget.PropertyValues[nameof(HotkeyButtonWidget.IconOffsetX)] = newX;
        widget.PropertyValues[nameof(HotkeyButtonWidget.IconOffsetY)] = newY;
        return true;
    }

    private static bool TryGetWidgetIconCenter(HotkeyButtonWidget hotkey, float width, float height, out SKPoint center, out float half)
    {
        float maxIconSize = Math.Min(width, height * 0.62f);
        float iconSize = hotkey.IconSize > 0 ? hotkey.IconSize : Math.Min(width, height) * 0.4f;
        iconSize = Math.Clamp(iconSize, 0f, maxIconSize);
        half = iconSize / 2f;
        if (half <= 0f)
        {
            center = default;
            return false;
        }
        center = new SKPoint(
            Math.Clamp(width / 2f + hotkey.IconOffsetX, half, width - half),
            Math.Clamp(height * 0.31f + hotkey.IconOffsetY, half, height - half));
        return true;
    }

    private static bool IsPointOverWidgetIcon(HotkeyButtonWidget hotkey, float width, float height, float localX, float localY)
    {
        if (string.IsNullOrWhiteSpace(hotkey.IconFile))
        {
            if (string.IsNullOrWhiteSpace(hotkey.Icon) || !GriddyIcons.Contains(hotkey.Icon))
                return false;
        }
        else if (!SvgIconLoader.TryGetPath(hotkey.IconFile, out _))
        {
            return false;
        }

        if (!TryGetWidgetIconCenter(hotkey, width, height, out var center, out float half))
            return false;

        float dx = localX - center.X;
        float dy = localY - center.Y;
        return dx * dx + dy * dy <= half * half;
    }
}
