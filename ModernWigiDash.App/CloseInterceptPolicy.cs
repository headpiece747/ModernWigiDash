namespace ModernWigiDash.App;

/// <summary>
/// The window's close-intercept decision (App, ADR-0018): whether a window
/// close (X, Alt+F4) or a minimize should hide to the tray instead of
/// closing or minimizing. The persisted value routes through
/// <see cref="CloseBehaviorPolicy"/> (a hand-edited profile can never
/// smuggle in a hide), and the tray icon must be live: with no tray the
/// user would have no way back to the window, so the action falls through
/// to the normal behavior (the N1 fallback). One predicate serves both
/// intercepts, so the two can never drift.
/// </summary>
internal static class CloseInterceptPolicy
{
    /// <summary>True when the close or minimize should hide to the tray:
    /// the resolved behavior is the tray keep-alive AND the tray icon is
    /// live.</summary>
    public static bool ShouldHide(string? persistedBehavior, bool trayLive)
        => trayLive && string.Equals(CloseBehaviorPolicy.Resolve(persistedBehavior), CloseBehaviorPolicy.HideToTray, StringComparison.Ordinal);
}
