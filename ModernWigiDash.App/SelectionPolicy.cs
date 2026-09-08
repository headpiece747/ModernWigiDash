namespace ModernWigiDash.App;

/// <summary>
/// The window's selection policy: the one owner of the same-reference early-out
/// that keeps the mutation contract's selection re-application free when nothing
/// changed, and protects the re-entrant import path (the funnel's control resyncs
/// can fire a handler that re-enters the contract while the old, now disposed,
/// selected instance is still referenced, and re-applying it must not rebuild the
/// inspector over a dead widget). Pure decision over the current and new
/// selections, so the rule is assertable without a window or a compositor.
/// MainWindow binds the production state (the _selectedWidget field, the
/// compositor's SelectedWidget, the inspector refresh, the canvas repaint) and
/// routes every selection change through this module first.
/// </summary>
internal static class SelectionPolicy
{
    /// <summary>The same-reference early-out: true when the new selection is the
    /// same instance as the current one (including both null), in which case the
    /// caller should skip the state write and the UI refresh.</summary>
    public static bool ShouldSkip(PlacedWidgetInstance? current, PlacedWidgetInstance? next)
        => ReferenceEquals(current, next);
}
