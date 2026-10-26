namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Optional widget capability: commits the persisted task JSON string through
/// the widget's one commit rule (the provider write, the state set, the
/// refresh). The App mapper talks to this capability, not to a concrete
/// widget type: the editor-kind dispatch names the editor, the capability
/// names the commit rule, and no widget-specific typeof check crosses the
/// seam (the capability-interface house rule, CONTEXT.md). The caller (the
/// inspector's persist leg) writes the same string to the profile.
/// </summary>
public interface ITaskCommitter
{
    /// <summary>
    /// Commits the persisted task JSON string. The widget parses it back to
    /// typed items for the provider write and sets its profile cache to the
    /// same string (no serialization ownership: the rows leg produced it).
    /// </summary>
    /// <param name="tasksJson">The persisted task JSON string to commit.</param>
    void CommitTasks(string tasksJson);
}
