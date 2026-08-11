namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// The widget size floors shared by the authoring surfaces — the single owner
/// of the minimum-size policy. Two deliberately distinct floors: the
/// inspector's typed-value validation floor (InspectorValuePolicy) and the
/// drag-resize usability floor (InputController — handles must stay
/// grabbable). One owner, so the values can never drift between call sites.
/// </summary>
public static class WidgetSizeLimits
{
    /// <summary>Minimum width/height a widget may be resized to via the inspector.</summary>
    public static float MinInspectorSize { get; } = 20f;

    /// <summary>Minimum width a widget may be dragged to in edit mode (resize handles stay grabbable).</summary>
    public static float MinDragSizeX { get; } = 40f;

    /// <summary>Minimum height a widget may be dragged to in edit mode (resize handles stay grabbable).</summary>
    public static float MinDragSizeY { get; } = 30f;
}
