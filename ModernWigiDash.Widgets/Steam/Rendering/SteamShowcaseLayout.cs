using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>The showcase's touch zones, in hit-test precedence order.</summary>
public enum SteamShowcaseHit
{
    /// <summary>The point is on no card.</summary>
    None,

    /// <summary>The flanking left card — a swipe/tap snaps to the previous title.</summary>
    Previous,

    /// <summary>The centered focal card — a tap launches the focused title.</summary>
    Focus,

    /// <summary>The flanking right card — a swipe/tap snaps to the next title.</summary>
    Next,
}

/// <summary>
/// The showcase's card geometry, computed once per frame from the placement
/// bounds and whether a previous/next title flanks the focus. The render path
/// draws the cards from these rects and the touch path hit-tests the same
/// rects, so the drawn cards and the tap targets can never drift apart.
/// </summary>
public readonly record struct SteamShowcaseGeometry(
    SteamHeaderGeometry Header,
    SKRect FocusCard,
    SKRect LeftCard,
    SKRect RightCard,
    SKRect LeftChevron,
    SKRect RightChevron,
    bool LeftVisible,
    bool RightVisible);

/// <summary>
/// Pure layout rules for the Steam showcase: the centered focal card, the
/// flanking cards (visible only when a neighbor exists), chevron navigation, and the hit test.
/// The design constants live only here, never re-derived at a draw site.
/// </summary>
public static class SteamShowcaseLayout
{
    /// <summary>The design-space width — the scale base for both render and touch.</summary>
    public const float DesignWidth = DisplayGeometry.FramebufferWidth;

    /// <summary>The design-space height — the scale base for both render and touch.</summary>
    public const float DesignHeight = DisplayGeometry.FramebufferHeight;

    /// <summary>The poster aspect: height is 1.5× the width (the 600×900 shape).</summary>
    public const float PosterAspect = 1.5f;

    /// <summary>
    /// The showcase geometry for a placement: the centered focal card (the
    /// largest that keeps the 2:3 aspect inside the bounds) and the flanking
    /// cards, which render only when a neighbor exists on that side.
    /// </summary>
    public static SteamShowcaseGeometry Compute(SKRect bounds, bool hasPrevious, bool hasNext)
    {
        float scale = bounds.Height > 0 ? bounds.Height / DesignHeight : 1f;
        float pad = 16f * scale;

        SteamHeaderGeometry header = SteamHeaderLayout.Compute(bounds);

        float contentTop = header.HeaderBounds.Bottom + 10f * scale;
        float contentBottom = bounds.Bottom - pad;
        float contentHeight = Math.Max(1f, contentBottom - contentTop);
        float contentMidY = contentTop + contentHeight / 2f;

        float focusWidth = Math.Min((bounds.Width - pad * 2f) * 0.45f, contentHeight / PosterAspect);
        focusWidth = Math.Max(1f, focusWidth);
        float focusHeight = focusWidth * PosterAspect;
        float focusX = bounds.MidX - focusWidth / 2f;
        float focusY = contentMidY - focusHeight / 2f;
        var focus = new SKRect(focusX, focusY, focusX + focusWidth, focusY + focusHeight);

        float flankWidth = focusWidth * 0.62f;
        float flankHeight = flankWidth * PosterAspect;
        float gap = 20f * scale;
        float flankY = contentMidY - flankHeight / 2f;
        var left = new SKRect(focusX - gap - flankWidth, flankY, focusX - gap, flankY + flankHeight);
        var right = new SKRect(focus.Right + gap, flankY, focus.Right + gap + flankWidth, flankY + flankHeight);

        float chevronSize = 48f * scale;
        float chevronY = contentMidY - chevronSize / 2f;
        var leftChevron = new SKRect(bounds.Left + pad, chevronY, bounds.Left + pad + chevronSize, chevronY + chevronSize);
        var rightChevron = new SKRect(bounds.Right - pad - chevronSize, chevronY, bounds.Right - pad, chevronY + chevronSize);

        return new SteamShowcaseGeometry(header, focus, left, right, leftChevron, rightChevron, hasPrevious, hasNext);
    }

    /// <summary>
    /// Hit-tests a touch point against chevrons, flanking cards, and the focal card, in precedence order.
    /// </summary>
    public static SteamShowcaseHit HitTest(SteamShowcaseGeometry geometry, SKPoint point)
    {
        if (geometry.LeftVisible && (geometry.LeftChevron.Contains(point) || geometry.LeftCard.Contains(point)))
            return SteamShowcaseHit.Previous;

        if (geometry.RightVisible && (geometry.RightChevron.Contains(point) || geometry.RightCard.Contains(point)))
            return SteamShowcaseHit.Next;

        if (geometry.FocusCard.Contains(point))
            return SteamShowcaseHit.Focus;

        return SteamShowcaseHit.None;
    }
}
