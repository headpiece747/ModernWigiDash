using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The one owner of the calendar feed list's persisted JSON shape: parses the
/// <c>FeedsJson</c> value into typed <see cref="CalendarFeed"/> records and
/// serializes them back. Both consumers of the shape route through this module
/// instead of hand-rolling their own read/write -- the widget's producer parse
/// (<see cref="CalendarWidget.ParseFeeds"/>) and the inspector's feed editor
/// (which maps the codec's records to its display drafts) -- so the kind
/// discriminator, the per-field defaults (port 443, enabled true), and the
/// arm-specific fields have one spelling and cannot drift between the two.
/// A malformed or absent value yields an empty list (the unavailable display),
/// never a throw.
/// </summary>
internal static class CalendarFeedsCodec
{
    /// <summary>Parses the persisted <c>FeedsJson</c> value into feed records.
    /// A malformed or absent value yields an empty list, never a throw.</summary>
    public static IReadOnlyList<CalendarFeed> Parse(string? feedsJson)
    {
        if (string.IsNullOrWhiteSpace(feedsJson))
            return [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(feedsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            List<CalendarFeed> feeds = [];
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;

                string kind = ReadString(el, "kind");
                string feedId = ReadString(el, "feedId");
                string label = ReadString(el, "label");
                string color = ReadString(el, "color");
                bool enabled = !el.TryGetProperty("enabled", out var en) || en.GetBoolean();

                CalendarFeed feed = string.Equals(kind, "caldav", StringComparison.Ordinal)
                    ? new CalDavFeed
                    {
                        FeedId = feedId,
                        Label = label,
                        ColorHex = color,
                        Enabled = enabled,
                        Server = ReadString(el, "server"),
                        Port = ReadInt(el, "port", 443),
                        PrincipalPath = ReadString(el, "principalPath"),
                        Username = ReadString(el, "username"),
                        SelectedCalendars = ReadStringList(el, "selectedCalendars"),
                    }
                    : new IcsUrlFeed
                    {
                        FeedId = feedId,
                        Label = label,
                        ColorHex = color,
                        Enabled = enabled,
                        Url = ReadString(el, "url"),
                    };
                feeds.Add(feed);
            }
            return feeds;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes feed records back to the persisted <c>FeedsJson</c>
    /// value. Every record is written (the caller decides which records are
    /// complete; the codec does not filter).</summary>
    public static string Serialize(IReadOnlyList<CalendarFeed> feeds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (CalendarFeed feed in feeds)
            {
                switch (feed)
                {
                    case CalDavFeed dav:
                        WriteObject(writer, [
                            ("kind", "caldav"),
                            ("feedId", dav.FeedId),
                            ("label", dav.Label),
                            ("color", dav.ColorHex),
                            ("server", dav.Server),
                            ("port", dav.Port),
                            ("principalPath", dav.PrincipalPath),
                            ("username", dav.Username),
                            ("enabled", dav.Enabled),
                        ]);
                        break;
                    case IcsUrlFeed ics:
                        WriteObject(writer, [
                            ("kind", "ics"),
                            ("feedId", ics.FeedId),
                            ("label", ics.Label),
                            ("color", ics.ColorHex),
                            ("url", ics.Url),
                            ("enabled", ics.Enabled),
                        ]);
                        break;
                }
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteObject(Utf8JsonWriter writer, (string Name, object Value)[] fields)
    {
        writer.WriteStartObject();
        foreach ((string name, object value) in fields)
        {
            switch (value)
            {
                case int i:
                    writer.WriteNumber(name, i);
                    break;
                case bool b:
                    writer.WriteBoolean(name, b);
                    break;
                default:
                    writer.WriteString(name, (string)value);
                    break;
            }
        }
        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement obj, string name, string fallback = "")
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : fallback;

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        var parts = new List<string>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? "");
        }
        return parts;
    }
}
