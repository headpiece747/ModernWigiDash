namespace ModernWigiDash.Sdk;

/// <summary>
/// A widget that claims touch gesture priority (e.g. internal horizontal scrolling,
/// carousels, scrubbers) and suppresses host page navigation swipes during its drag session.
/// </summary>
public interface IGestureClaimant
{
    /// <summary>
    /// True when the widget claims the current horizontal drag gesture, preventing
    /// the host gesture interpreter from promoting the touch sequence to a page swipe.
    /// </summary>
    bool ClaimsHorizontalDrag { get; }
}
