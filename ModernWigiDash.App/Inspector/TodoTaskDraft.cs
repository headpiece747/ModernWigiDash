namespace ModernWigiDash.App.Inspector;

/// <summary>
/// An editable task row in the Task Manager inspector editor.
/// Plain data shape for WPF data binding.
/// </summary>
internal sealed class TodoTaskDraft
{
    /// <summary>Unique task identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Task title description.</summary>
    public string Title { get; set; } = "";

    /// <summary>Whether the task is marked as done.</summary>
    public bool IsDone { get; set; }

    /// <summary>Optional category tag (e.g. Work, Code, Pet).</summary>
    public string Category { get; set; } = "";

    /// <summary>Due date, carried through the edit round-trip (not exposed in
    /// the row UI; an un-edited value must survive a commit).</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Estimated minutes, carried through the edit round-trip (not
    /// exposed in the row UI; an un-edited value must survive a commit).</summary>
    public int EstimatedMinutes { get; set; }
}
