using System.Text.Json;

namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The pure wire-to-domain mapper: the Super Productivity /tasks JSON
/// shape to <see cref="TodoItem"/>. The walk tolerates the shape's loose
/// parts (a bare array, a {data: [...]} envelope, missing fields, the
/// millisecond time estimate, the first tag as category). An unparseable
/// body throws <see cref="JsonException"/> — the wire is untrusted, and
/// the caller decides what a fault means (the provider keeps its cached
/// list).
/// </summary>
internal static class SuperProductivityTaskMapper
{
    /// <summary>
    /// Maps a /tasks JSON body to TodoItems. A missing or empty array
    /// yields an empty list; an unparseable body throws JsonException.
    /// </summary>
    public static IReadOnlyList<TodoItem> Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<TodoItem>();

        JsonElement root = doc.RootElement;
        JsonElement arrayElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            arrayElement = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
        {
            arrayElement = dataElem;
        }
        else
        {
            arrayElement = default;
        }

        if (arrayElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arrayElement.EnumerateArray())
            {
                string id = el.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? "") : Guid.NewGuid().ToString("N");
                string title = el.TryGetProperty("title", out var titleProp) ? (titleProp.GetString() ?? "") : "Task";
                bool isDone = el.TryGetProperty("isDone", out var doneProp) && doneProp.GetBoolean();
                int estimatedMins = 0;
                if (el.TryGetProperty("timeEstimate", out var estProp) && estProp.ValueKind == JsonValueKind.Number && estProp.TryGetInt64(out long ms))
                {
                    estimatedMins = (int)(ms / 60000);
                }

                string? category = null;
                if (el.TryGetProperty("tagIds", out var tagIdsProp) && tagIdsProp.ValueKind == JsonValueKind.Array)
                {
                    var firstTag = tagIdsProp.EnumerateArray().FirstOrDefault();
                    if (firstTag.ValueKind == JsonValueKind.String)
                    {
                        category = firstTag.GetString();
                    }
                }

                list.Add(new TodoItem(id, title, isDone, category, EstimatedMinutes: estimatedMins));
            }
        }

        return list;
    }
}
