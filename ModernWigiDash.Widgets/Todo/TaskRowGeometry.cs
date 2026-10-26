namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Pre-computed geometry for one task item row in the companion task widget.
/// </summary>
internal readonly record struct TaskRowGeometry(
    string TaskId,
    SKRect RowRect,
    SKRect CheckboxRect,
    bool IsDone,
    string Title,
    string? Category);
