namespace ModernWigiDash.Core.Models;

/// <summary>
/// Widget contract for edit-mode icon dragging. The widget owns its icon
/// geometry — hit region, center, and the grab-move math (offsets, clamping,
/// PropertyValues bookkeeping) — so the input module sees only the capability,
/// never the widget type, and the layout math lives once, in the widget that
/// draws the icon. Mirrors the <c>ResizeHandleSize</c> ownership pattern
/// (EditOverlay owns it, the compositor forwards it).
/// </summary>
public interface IWidgetIconGrab
{
    /// <summary>True when the local point is inside the drawn icon's hit region.</summary>
    bool IsPointOverIcon(float width, float height, float localX, float localY);

    /// <summary>Icon center and half-size for the given widget bounds; false when no icon is drawn.</summary>
    bool TryGetIconCenter(float width, float height, out SKPoint center, out float half);

    /// <summary>
    /// Applies a grab-move: derives the new icon offsets from the pointer
    /// position and the grab anchor (the offset between the grab point and the
    /// icon center at grab start), updates widget state (through
    /// <see cref="ModernWidgetBase.SetProperty"/>, so PropertyValues
    /// persistence is included), and returns true when the offsets changed.
    /// </summary>
    bool ApplyGrabMove(PlacedWidgetInstance placed, float localX, float localY, float grabOffsetX, float grabOffsetY);
}
