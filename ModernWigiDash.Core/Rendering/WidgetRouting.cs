using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Pure page-input routing: hit-testing and touch delivery over a page's
/// placed widgets. No rendering state and no instance — the compositor draws
/// the frame, this module answers "what is under this point" and delivers the
/// touch in the widget's rotated-local coordinates. The two consumers of the
/// render transform (Compose and routing) stay one geometry via
/// <see cref="PlacedWidgetInstance.ToLocalPoint"/>.
/// </summary>
public static class WidgetRouting
{
    /// <summary>
    /// Top-most widget (highest ZIndex) whose drawn footprint contains the
    /// point — the rotation-aware hit shape, not the unrotated bounding box.
    /// A ZIndex tie goes to the LAST widget in list order, matching the
    /// compositor's stable ascending ZIndex paint order (the later widget is
    /// painted on top). Single pass, zero allocation (replaces
    /// OrderByDescending+FirstOrDefault).
    /// </summary>
    public static PlacedWidgetInstance? HitTest(PageLayout page, float pointX, float pointY)
    {
        PlacedWidgetInstance? best = null;
        foreach (PlacedWidgetInstance widget in page.Widgets)
        {
            if (widget.ActiveInstance is null) continue;
            if (!widget.ContainsPoint(pointX, pointY)) continue;
            if (best == null || widget.ZIndex >= best.ZIndex)
            {
                best = widget;
            }
        }
        return best;
    }

    /// <summary>
    /// Delivers a touch to the top-most widget under the point, in that
    /// widget's rotated-local coordinates. Points outside every widget (or on
    /// a widget without an active instance) are dropped.
    /// </summary>
    public static void RouteTouch(PageLayout page, float pointX, float pointY, TouchEventType eventType)
    {
        var target = HitTest(page, pointX, pointY);
        if (target?.ActiveInstance != null)
        {
            var localPoint = target.ToLocalPoint(pointX, pointY);
            target.ActiveInstance.OnTouch(localPoint, eventType);
        }
    }
}
