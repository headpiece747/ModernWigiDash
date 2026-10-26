namespace ModernWigiDash.App.Inspector;

using ModernWigiDash.Widgets.Todo;

/// <summary>
/// Pure decision rules and serialization round-trip for the Task Manager inspector editor.
/// Maps between persisted tasks JSON and editable <see cref="TodoTaskDraft"/> objects.
/// </summary>
internal static class TodoTaskEditorModel
{
    /// <summary>
    /// Parses the persisted tasks JSON into display drafts.
    /// </summary>
    public static IReadOnlyList<TodoTaskDraft> Parse(string? json)
    {
        var items = TodoItemCodec.Parse(json);
        List<TodoTaskDraft> drafts = [];
        foreach (var item in items)
        {
            drafts.Add(new TodoTaskDraft
            {
                Id = item.Id,
                Title = item.Title,
                IsDone = item.IsDone,
                Category = item.Category ?? "",
                DueDate = item.DueDate,
                EstimatedMinutes = item.EstimatedMinutes,
            });
        }
        return drafts;
    }

    /// <summary>
    /// Maps editable drafts to typed <see cref="TodoItem"/>s for the commit seam.
    /// Incomplete drafts with empty titles are excluded.
    /// </summary>
    public static IReadOnlyList<TodoItem> ToItems(IReadOnlyList<TodoTaskDraft> drafts)
    {
        List<TodoItem> items = [];
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Title))
                continue;

            items.Add(new TodoItem(
                Id: string.IsNullOrWhiteSpace(draft.Id) ? Guid.NewGuid().ToString("N") : draft.Id,
                Title: draft.Title.Trim(),
                IsDone: draft.IsDone,
                Category: string.IsNullOrWhiteSpace(draft.Category) ? null : draft.Category.Trim(),
                DueDate: draft.DueDate,
                EstimatedMinutes: draft.EstimatedMinutes));
        }
        return items;
    }
}
