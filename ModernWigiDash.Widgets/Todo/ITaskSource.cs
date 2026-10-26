namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The task-source face of the task provider: completion, wholesale
/// replacement of the task collection, and the refresh contract. Retrieval
/// (<c>GetTasksAsync</c>) and the synchronous snapshot (<c>GetSnapshot</c>)
/// are concrete-provider faces, not part of the interface: the refresh
/// owner's published snapshot is the one read site for "the current tasks".
/// </summary>
public interface ITaskSource
{
    /// <summary>Marks a task as completed or incomplete.</summary>
    ValueTask<TodoItem?> CompleteTaskAsync(string taskId, bool isDone, CancellationToken ct = default);

    /// <summary>
    /// Replaces the task collection wholesale (the inspector's write path).
    /// Per-provider semantics: Local persists to the store (the task-state
    /// owner); Super Productivity replaces the in-memory cache only (the
    /// server is the source of truth and the next poll reconciles the view).
    /// </summary>
    void SetTasks(IReadOnlyList<TodoItem> tasks);

    /// <summary>
    /// The refresh contract: one call that does the provider's refresh work
    /// (Local: none, its state is in memory; Super Productivity: one poll of
    /// the server) and returns the fresh snapshot. The refresh owner's tick
    /// is this one call — the snapshot is the return value, never a
    /// discarded side effect of separate calls.
    /// </summary>
    ValueTask<TodoCompanionSnapshot> RefreshAsync(CancellationToken ct = default);
}
