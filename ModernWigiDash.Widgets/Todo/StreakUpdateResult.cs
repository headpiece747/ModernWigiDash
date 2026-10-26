namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Outcome of updating the companion's daily streak.
/// </summary>
public readonly record struct StreakUpdateResult(
    int NewStreak,
    int ShieldsRemaining,
    bool ShieldConsumed,
    bool StreakPreserved);
