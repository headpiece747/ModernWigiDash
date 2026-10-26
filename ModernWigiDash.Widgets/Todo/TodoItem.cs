namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// A single companion task item.
/// </summary>
public sealed record TodoItem(
    string Id,
    string Title,
    bool IsDone,
    string? Category = null,
    DateTime? DueDate = null,
    int EstimatedMinutes = 0);
