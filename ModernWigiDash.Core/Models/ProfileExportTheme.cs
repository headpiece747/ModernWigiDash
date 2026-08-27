using System.Text.Json;
using System.Text.Json.Nodes;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Core.Models;

/// <summary>
/// The theme section of the profile export bundle (ADR-0021): the one
/// spelling of how the current theme rides a profile export and how an
/// import reads it back. The theme is a deliberate per-item restore item
/// (the JetBrains/VS Code pattern): the profile import itself never touches
/// the theme file; the window offers the restore behind the user's confirm.
/// The persisted profile.json never carries this section (ProfilePersistence
/// saves through the plain <see cref="ProfileOps.ExportJson"/>), so a boot
/// load can never restore a theme. A bundled theme is untrusted input: it is
/// extracted as plain strings here and validated per-property at apply time
/// (the ParseColor rule through the ThemeManager's skip-invalid-hex); the
/// import boundary's file-size guard already bounds the payload.
/// </summary>
public static class ProfileExportTheme
{
    /// <summary>The bundle's theme section key (a top-level sibling of the
    /// profile's own fields).</summary>
    public const string JsonKey = "theme";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Adds the theme section to an exported profile JSON (the root object
    /// gains a "theme" property). The profile's own fields are untouched; a
    /// non-object root or unparseable input passes through unchanged
    /// (defensive: the exporter always produces an object).
    /// </summary>
    public static string WithTheme(string profileJson, ThemeSettings theme)
    {
        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return profileJson;
            JsonObject root = doc.RootElement.Deserialize<JsonObject>()!;
            // Serialize the theme with the plain options (the ExportJson shape)
            // and parse it into a plain node: a JsonValue.Create without
            // options would capture a customized value that refuses to write
            // under the bundle's indent options (.NET 10 resolver strictness).
            root[JsonKey] = JsonNode.Parse(JsonSerializer.Serialize(theme, Indented));
            return root.ToJsonString(Indented);
        }
        catch (JsonException)
        {
            // A malformed export passes through untouched rather than failing
            // the export (the exporter never produces one).
            return profileJson;
        }
    }

    /// <summary>
    /// Reads the theme section from a bundle JSON. Null when the key is
    /// absent, null-valued, or unshaped (legacy exports and the app's own
    /// profile.json never carry it). Never throws: the import boundary calls
    /// this on its one size-guarded read.
    /// </summary>
    public static ThemeSettings? ReadTheme(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(JsonKey, out var node)) return null;
            return node.Deserialize<ThemeSettings>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
