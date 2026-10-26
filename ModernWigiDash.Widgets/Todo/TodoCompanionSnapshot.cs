namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Immutable, thread-safe snapshot of tasks, pet companion, and focus timer
/// state for a single render frame. The companion mood is deliberately NOT
/// in this snapshot: the render tick owns the mood decision (it re-derives
/// it from the freshest focus/companion state it is drawing), so a snapshot
/// computed on the provider's 2s-stale refresh cadence would carry a mood
/// the renderer would immediately override.
/// </summary>
public readonly record struct TodoCompanionSnapshot(
    IReadOnlyList<TodoItem> Tasks,
    CozyCompanionState Companion,
    FocusState Focus)
{
    /// <summary>Empty snapshot fallback when no provider is active.</summary>
    public static readonly TodoCompanionSnapshot Empty = new(
        [],
        new CozyCompanionState(),
        new FocusState(false, TimeSpan.Zero, TimeSpan.FromMinutes(25)));
}
