namespace ModernWigiDash.Widgets.Todo;

using System.Text.Json;

/// <summary>
/// Serializer and parser for TodoItem collections stored in widget properties and JSON stores.
/// </summary>
public static class TodoItemCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Parses a JSON array string into a read-only list of <see cref="TodoItem"/>s.
    /// Returns an empty list on null, whitespace, or malformed JSON.
    /// </summary>
    public static IReadOnlyList<TodoItem> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            List<TodoItem>? items = JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOptions);
            return items ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Serializes a collection of <see cref="TodoItem"/>s into a compact JSON array string.
    /// </summary>
    public static string Serialize(IReadOnlyList<TodoItem> items)
    {
        if (items.Count == 0)
            return "[]";

        return JsonSerializer.Serialize(items, JsonOptions);
    }
}
