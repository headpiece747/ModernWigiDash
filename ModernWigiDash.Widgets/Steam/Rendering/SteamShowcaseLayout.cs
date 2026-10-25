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
    SKRect FocusCard,
    SKRect LeftCard,
    SKRect RightCard,
    bool LeftVisible,
    bool RightVisible);

/// <summary>
/// Pure layout rules for the Steam showcase: the centered focal card, the
/// flanking cards (visible only when a neighbor exists), and the hit test.
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
        float pad = 24f * (bounds.Height / DesignHeight);
        float focusWidth = Math.Min((bounds.Width - pad * 2f) * 0.5f, (bounds.Height - pad * 2f) / PosterAspect);
        focusWidth = Math.Max(1f, focusWidth);
        float focusHeight = focusWidth * PosterAspect;
        float focusX = bounds.MidX - focusWidth / 2f;
        float focusY = bounds.MidY - focusHeight / 2f;
        var focus = new SKRect(focusX, focusY, focusX + focusWidth, focusY + focusHeight);

        float flankWidth = focusWidth * 0.6f;
        float flankHeight = flankWidth * PosterAspect;
        float gap = 16f * (bounds.Height / DesignHeight);
        float flankY = bounds.MidY - flankHeight / 2f;
        var left = new SKRect(focusX - gap - flankWidth, flankY, focusX - gap, flankY + flankHeight);
        var right = new SKRect(focus.Right + gap, flankY, focus.Right + gap + flankWidth, flankY + flankHeight);

        return new SteamShowcaseGeometry(focus, left, right, hasPrevious, hasNext);
    }

    /// <summary>
    /// Hit-tests a touch point against the flanking cards (only when visible)
    /// and the focal card, in precedence order.
    /// </summary>
    public static SteamShowcaseHit HitTest(SteamShowcaseGeometry geometry, SKPoint point)
    {
        if (geometry.LeftVisible && geometry.LeftCard.Contains(point))
            return SteamShowcaseHit.Previous;

        if (geometry.RightVisible && geometry.RightCard.Contains(point))
            return SteamShowcaseHit.Next;

        if (geometry.FocusCard.Contains(point))
            return SteamShowcaseHit.Focus;

        return SteamShowcaseHit.None;
    }
}
