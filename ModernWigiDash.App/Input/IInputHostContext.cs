using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.Input;

/// <summary>
/// Host orchestration context for <see cref="InputController"/>: supplies active page state,
/// handles page switching and selection, and routes manipulation callbacks.
/// </summary>
internal interface IInputHostContext
{
    /// <summary>Gets the current input state snapshot (active page, page counts, indices).</summary>
    InputState GetState();

    /// <summary>Returns true if the desktop shell is currently in layout edit mode.</summary>
    bool IsDesktopEditMode();

    /// <summary>Navigates to the designated page index.</summary>
    void NavigateTo(int pageIndex);

    /// <summary>Requests a presentation canvas redraw.</summary>
    void RequestRender();

    /// <summary>Routes a touch event to a placed widget instance on the page.</summary>
    void RouteTouch(PageLayout page, float x, float y, TouchEventType eventType);

    /// <summary>Hit-tests a point against widgets on the page.</summary>
    PlacedWidgetInstance? HitTest(PageLayout page, float x, float y);

    /// <summary>Selects the target widget instance for desktop authoring.</summary>
    void SelectWidget(PlacedWidgetInstance? widget);

    /// <summary>Dispatches a manipulation change during layout editing.</summary>
    void OnManipulation(ManipulationChange change);
}
