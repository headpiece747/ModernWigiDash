namespace ModernWigiDash.App.Inspector;

using ModernWigiDash.Widgets.Todo;

/// <summary>
/// Row-state manager for the Task Manager inspector editor.
/// Owns the editable draft list and commits the persisted task JSON string on
/// mutation. The rows leg owns the serialization: the drafts map to typed
/// items, then serialize through the codec, so the commit payload is the
/// persisted string (uniform with the calendar feed editor).
/// </summary>
internal sealed class TodoTaskEditorRows : ListPropertyEditorRows<TodoTaskDraft>
{
    /// <summary>
    /// Initializes a new instance of <see cref="TodoTaskEditorRows"/>.
    /// </summary>
    public TodoTaskEditorRows(string? tasksJson, Action<string> commit)
        : base(TodoTaskEditorModel.Parse(tasksJson), commit)
    {
    }

    /// <summary>Appends a new blank task draft.</summary>
    public void Add() => Add(new TodoTaskDraft { Title = "New Task" });

    /// <summary>Toggles completion status for a draft.</summary>
    public void SetDone(int index, bool isDone) => Mutate(index, draft => draft.IsDone = isDone);

    /// <summary>Updates title description for a draft.</summary>
    public void SetTitle(int index, string title) => Mutate(index, draft => draft.Title = title);

    /// <summary>Updates category for a draft.</summary>
    public void SetCategory(int index, string category) => Mutate(index, draft => draft.Category = category);

    protected override string Serialize(IReadOnlyList<TodoTaskDraft> drafts)
        => TodoItemCodec.Serialize(TodoTaskEditorModel.ToItems(drafts));
}
